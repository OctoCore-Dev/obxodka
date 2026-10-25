using System.Net.WebSockets;

namespace obxodka.Core.Transports;

public sealed class ObxodkaWebSocketTransport(int serverPort = 443, string? configuredSni = null) : IVpnTransport
{
    public string ProtocolName => "OBXODKA-WS";
    public bool IsConnected => Volatile.Read(ref _connected);
    public int EffectivePort { get; private set; } = serverPort;
    public string ActiveSni { get; private set; } = configuredSni ?? AppConfig.PrimaryDomain;

    public event Action<byte[], int>? OnPacketReceived;
    public event Action<long>? OnPingUpdated;
    public event Action? OnConnectionDropped;
    public event Action<int>? OnMtuUpdated;

    private readonly Channel<(byte[] buffer, int length)> _txChannel =
        Channel.CreateBounded<(byte[] buffer, int length)>(new BoundedChannelOptions(8192)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    private TaskCompletionSource<(string ip, string ip6)> _ipTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string? _configuredSni = configuredSni;
    private readonly int _serverPort = serverPort;
    private Action<Socket>? _protectAction;

    private ClientWebSocket? _webSocket;
    private HttpMessageInvoker? _wsInvoker;
    private CancellationTokenSource? _cts;
    private bool _connected;
    private bool _disposed;
    private string _activeServerHost = AppConfig.PrimaryDomain;
    private volatile string? _assignedIp;
    private uint _sessionKey;
    private uint _candidateSessionKey;
    private readonly List<int> _receivedBurstMtus = [];
    private static readonly string[] t_wsPaths = ["/api/v1/stream", "/v1/telemetry", "/g/collect", "/api/v1/sync", "/v2/event", "/api/v1/ws"];

    public static Action<Socket>? OnSocketCreated { get; set; }

    public void ProtectSockets(Action<Socket> protectAction) => _protectAction = protectAction;

    private HttpMessageInvoker CreateWsInvoker(string targetSni, string serverIp, int port)
    {
        var isDirectIp = IPAddress.TryParse(serverIp, out var literalIp);
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            Proxy = null,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = targetSni,
                EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                    CertificateValidator.ValidateServerCertificate(cert, chain, errors, dynamicPinningHash: string.Empty)
            },
            ConnectCallback = async (context, cToken) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                    SendBufferSize = 4194304,
                    ReceiveBufferSize = 4194304
                };
                OnSocketCreated?.Invoke(socket);
                ObxodkaStreamTransport.OnSocketCreated?.Invoke(socket);
                _protectAction?.Invoke(socket);

                var endPoint = isDirectIp && literalIp is not null
                    ? (EndPoint)new IPEndPoint(literalIp, port)
                    : new IPEndPoint(await ApiService.ResolveHostAntiPoisonAsync(targetSni, cToken).ConfigureAwait(false), port);

                try
                {
                    await socket.ConnectAsync(endPoint, cToken).ConfigureAwait(false);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }

                var netStream = new NetworkStream(socket, ownsSocket: true);
                return (!isDirectIp || (literalIp is not null && !IPAddress.IsLoopback(literalIp)))
                    ? new DpiBypassStream(netStream, bypassEnabled: true)
                    : netStream;
            }
        };

        return new HttpMessageInvoker(handler, disposeHandler: true);
    }

    public async Task<(string ip, string ip6)> ConnectAsync(string serverIp, string thumbprint, CancellationToken ct)
    {
        _activeServerHost = !string.IsNullOrWhiteSpace(_configuredSni)
            ? _configuredSni
            : (!IPAddress.TryParse(serverIp, out _) ? serverIp : AppConfig.PrimaryDomain);

        ActiveSni = _activeServerHost;
        var isDirectIp = IPAddress.TryParse(serverIp, out _);

        int[] portCandidates = _serverPort == 443 ? [443, 8443, 2053, 443, 443] : [_serverPort, 443, 8443, 2053, 443];
        Exception? lastEx = null;

        for (var pIdx = 0; pIdx < portCandidates.Length; pIdx++)
        {
            var candidatePort = portCandidates[pIdx];
            var mirrors = AppConfig.MirrorDomains;
            var mirrorHost = (!isDirectIp && pIdx is 1 or 2 && mirrors.Length > pIdx) ? mirrors[pIdx] : serverIp;
            var candidateServerIp = (!isDirectIp && pIdx == 3)
                ? AppConfig.DirectServerIp
                : (!isDirectIp && pIdx == 4 ? AppConfig.WorkerServerIp : mirrorHost);
            var candidateSni = (!isDirectIp && pIdx is 3 or 4)
                ? AppSecrets.GetRandomSni()
                : (!isDirectIp && pIdx is 1 or 2 ? mirrorHost : _activeServerHost);
            if (!isDirectIp && pIdx is 3 or 4)
            {
                candidatePort = 443;
            }
            var candidateIsDirectIp = IPAddress.TryParse(candidateServerIp, out _);
            var targetHostHeader = candidateIsDirectIp ? candidateSni : candidateServerIp;
            var connectHost = candidateIsDirectIp ? candidateSni : candidateServerIp;
            var wsPath = t_wsPaths[pIdx % t_wsPaths.Length];
            EffectivePort = candidatePort;
            ActiveSni = candidateSni;

            _cts = new CancellationTokenSource();
            _ipTcs = new TaskCompletionSource<(string ip, string ip6)>(TaskCreationOptions.RunContinuationsAsynchronously);

            var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            try
            {
                ws.Options.SetRequestHeader("Host", targetHostHeader);
            }
            catch { }
            ws.Options.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");

            var cid = Random.Shared.Next(10000000, 99999999);
            var uriBuilder = new UriBuilder("wss", connectHost, candidatePort, wsPath)
            {
                Query = $"ray=0&mtu={NetworkDefaults.CurrentMtu}&cid={cid}&v=133.0"
            };

            var invoker = CreateWsInvoker(candidateSni, candidateServerIp, candidatePort);
            using var attemptCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(3500));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token, attemptCts.Token);

            try
            {
                Shared.Logging.AppLogger.Log($"[OBXODKA-WS] Connecting to {uriBuilder.Uri} (SNI={ActiveSni}, Port={candidatePort})...");
                await ws.ConnectAsync(uriBuilder.Uri, invoker, linkedCts.Token).ConfigureAwait(false);

                _wsInvoker?.Dispose();
                _wsInvoker = invoker;
                _webSocket = ws;
                Volatile.Write(ref _connected, true);

                var sessionSeed = (uint)Random.Shared.Next(1, int.MaxValue);
                _candidateSessionKey = ObxodkaFraming.DeriveSessionKey(thumbprint, 0, sessionSeed);
                Volatile.Write(ref _sessionKey, 0u);
                var handshakeStr = string.IsNullOrEmpty(_assignedIp)
                    ? $"{thumbprint}|R:0|M:{NetworkDefaults.CurrentMtu}|K:{sessionSeed:X8}"
                    : $"{thumbprint}|{_assignedIp}|R:0|M:{NetworkDefaults.CurrentMtu}|K:{sessionSeed:X8}";
                var hsPayload = Encoding.UTF8.GetBytes(handshakeStr);
                var hsFrame = ObxodkaFraming.Pack(ObxodkaFraming.CmdHandshake, hsPayload, out var hsTotalLen);
                try
                {
                    await ws.SendAsync(hsFrame.AsMemory(0, hsTotalLen), WebSocketMessageType.Binary, true, linkedCts.Token).ConfigureAwait(false);
                    var chaffFrame = ObxodkaFraming.Pack(ObxodkaFraming.CmdChaff, [], out var chaffLen, NetworkDefaults.CurrentMtu, _candidateSessionKey);
                    try
                    {
                        await ws.SendAsync(chaffFrame.AsMemory(0, chaffLen), WebSocketMessageType.Binary, true, linkedCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(chaffFrame);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(hsFrame);
                }

                _ = Task.Run(() => ReaderLoopAsync(ws, _cts.Token), _cts.Token);
                _ = Task.Run(() => WriterLoopAsync(ws, _cts.Token), _cts.Token);
                _ = Task.Run(() => PingLoopAsync(ws, _cts.Token), _cts.Token);

                using (linkedCts.Token.Register(() => _ipTcs.TrySetCanceled()))
                {
                    var result = await _ipTcs.Task.ConfigureAwait(false);
                    Shared.Logging.AppLogger.Log($"[OBXODKA-WS] Connected on port {candidatePort}! Assigned IPv4: {result.ip}, IPv6: {result.ip6}");
                    return result;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && pIdx + 1 < portCandidates.Length)
            {
                lastEx = ex;
                Shared.Logging.AppLogger.LogWarning($"[OBXODKA-WS] Attempt on {connectHost}:{candidatePort}{wsPath} failed ({ex.Message}). Hopping to next port/path...");
                Volatile.Write(ref _connected, false);
                _cts.Cancel();
                ws.Dispose();
                invoker.Dispose();
            }
        }

        throw lastEx ?? new TimeoutException("WebSocket connection failed across all candidate ports.");
    }

    private async Task ReaderLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var rxBuf = ArrayPool<byte>.Shared.Rent(131072);
        var rxFilled = 0;
        var rxOffset = 0;

        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var receiveResult = await ws.ReceiveAsync(rxBuf.AsMemory(rxFilled, rxBuf.Length - rxFilled), ct).ConfigureAwait(false);
                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                rxFilled += receiveResult.Count;

                while (rxFilled - rxOffset >= ObxodkaFraming.HeaderSize)
                {
                    var headerSpan = rxBuf.AsSpan(rxOffset, ObxodkaFraming.HeaderSize);
                    var decodeKey = Volatile.Read(ref _sessionKey);
                    if (decodeKey == 0 && _candidateSessionKey != 0 &&
                        ObxodkaFraming.TryDecodeHeader(headerSpan, out var candTotal, out var candPayload, out var candCmd, out _, _candidateSessionKey) &&
                        candCmd == ObxodkaFraming.CmdHandshakeResponse && candTotal <= 1024)
                    {
                        decodeKey = _candidateSessionKey;
                        Volatile.Write(ref _sessionKey, _candidateSessionKey);
                    }
                    if (!ObxodkaFraming.TryDecodeHeader(headerSpan, out var totalLen, out var payloadLen, out var command, out var salt, decodeKey))
                    {
                        if (decodeKey == 0 && ObxodkaFraming.TryDecodeHeader(headerSpan, out totalLen, out payloadLen, out command, out salt, _candidateSessionKey))
                        {
                            decodeKey = _candidateSessionKey;
                            Volatile.Write(ref _sessionKey, _candidateSessionKey);
                        }
                        else
                        {
                            Shared.Logging.AppLogger.LogError("[OBXODKA-WS] Frame header decode error, closing.");
                            return;
                        }
                    }

                    if (rxFilled - rxOffset < totalLen)
                    {
                        break;
                    }

                    var payloadSpan = rxBuf.AsSpan(rxOffset + ObxodkaFraming.HeaderSize, payloadLen);
                    if (command != ObxodkaFraming.CmdChaff && payloadLen > 0)
                    {
                        ObxodkaFraming.UnmaskPayload(payloadSpan, salt, decodeKey);
                    }
                    rxOffset += totalLen;

                    if (command == ObxodkaFraming.CmdHandshakeResponse)
                    {
                        var info = Encoding.UTF8.GetString(payloadSpan);
                        if (info.StartsWith("IP:", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = info[3..].Split('|');
                            var assignedIp = parts[0];
                            var assignedIpV6 = parts.Length > 1 ? parts[1] : string.Empty;
                            foreach (var part in parts)
                            {
                                if (part.StartsWith("MTU:", StringComparison.OrdinalIgnoreCase) &&
                                    int.TryParse(part[4..], out var sMtu) &&
                                    sMtu >= NetworkDefaults.MinMtu && sMtu <= NetworkDefaults.MaxMtu)
                                {
                                    NetworkDefaults.CurrentMtu = sMtu;
                                }
                            }
                            _assignedIp = assignedIp;
                            _ = _ipTcs.TrySetResult((assignedIp, assignedIpV6));
                        }
                    }
                    else if (command == ObxodkaFraming.CmdData)
                    {
                        var packetBuf = ArrayPool<byte>.Shared.Rent(payloadLen);
                        payloadSpan.CopyTo(packetBuf);
                        OnPacketReceived?.Invoke(packetBuf, payloadLen);
                    }
                    else if (command == ObxodkaFraming.CmdPing)
                    {
                        if (payloadLen >= 8)
                        {
                            var echoBytes = ArrayPool<byte>.Shared.Rent(payloadLen);
                            try
                            {
                                payloadSpan.CopyTo(echoBytes);
                                var pongFrame = ObxodkaFraming.Pack(ObxodkaFraming.CmdPong, echoBytes.AsSpan(0, payloadLen), out var pongLen, sessionKey: _sessionKey);
                                try
                                {
                                    await ws.SendAsync(pongFrame.AsMemory(0, pongLen), WebSocketMessageType.Binary, true, CancellationToken.None).ConfigureAwait(false);
                                }
                                finally
                                {
                                    ArrayPool<byte>.Shared.Return(pongFrame);
                                }
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(echoBytes);
                            }
                        }
                    }
                    else if (command == ObxodkaFraming.CmdPong)
                    {
                        if (payloadLen >= 8)
                        {
                            var sentTicks = BinaryPrimitives.ReadInt64LittleEndian(payloadSpan);
                            var rtt = (Stopwatch.GetTimestamp() - sentTicks) * 1000 / Stopwatch.Frequency;
                            if (rtt is >= 0 and < 5000)
                            {
                                OnPingUpdated?.Invoke(rtt);
                            }
                        }
                    }
                    else if (command == ObxodkaFraming.CmdMtuSync)
                    {
                        if (payloadLen >= 2)
                        {
                            var sMtu = BinaryPrimitives.ReadUInt16BigEndian(payloadSpan);
                            if (sMtu is >= NetworkDefaults.MinMtu and <= NetworkDefaults.MaxMtu)
                            {
                                NetworkDefaults.CurrentMtu = sMtu;
                                OnMtuUpdated?.Invoke(sMtu);
                            }
                        }
                    }
                    else if (command == ObxodkaFraming.CmdMtuBurstPayload)
                    {
                        if (payloadLen >= 4 && payloadSpan[0] == 0xEE && payloadSpan[1] == 0x55)
                        {
                            var targetMtu = BinaryPrimitives.ReadUInt16BigEndian(payloadSpan.Slice(2, 2));
                            lock (_receivedBurstMtus)
                            {
                                if (!_receivedBurstMtus.Contains(targetMtu))
                                {
                                    _receivedBurstMtus.Add(targetMtu);
                                }
                            }
                        }
                    }
                }

                if (rxOffset > 0)
                {
                    var remaining = rxFilled - rxOffset;
                    if (remaining > 0)
                    {
                        rxBuf.AsSpan(rxOffset, remaining).CopyTo(rxBuf);
                    }
                    rxFilled = remaining;
                    rxOffset = 0;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Shared.Logging.AppLogger.LogWarning($"[OBXODKA-WS] Reader loop terminated: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rxBuf);
            if (Volatile.Read(ref _connected))
            {
                Volatile.Write(ref _connected, false);
                OnConnectionDropped?.Invoke();
            }
        }
    }

    private async Task WriterLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var coalesceBuf = ArrayPool<byte>.Shared.Rent(65536);
        var reader = _txChannel.Reader;

        try
        {
            long wsTxBytes = 0;
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                var coalescedLen = 0;

                while (reader.TryRead(out var item))
                {
                    var (packet, length) = item;
                    if (packet == null || length <= 0)
                    {
                        continue;
                    }

                    try
                    {
                        var isMtu = length == 4 && packet[0] == 0xCC && packet[1] == 0xDD;
                        var isBurstReq = length == 2 && packet[0] == 0xEE && packet[1] == 0x77;
                        var isPing = (length >= 10 && packet[0] == 0xAA && packet[1] == 0xBB) || length == 8;
                        var cmd = isMtu ? ObxodkaFraming.CmdMtuSync : (isBurstReq ? ObxodkaFraming.CmdMtuBurstRequest : (isPing ? ObxodkaFraming.CmdPing : ObxodkaFraming.CmdData));
                        var payloadOffset = isMtu ? 2 : (isBurstReq ? 0 : ((length >= 10 && packet[0] == 0xAA && packet[1] == 0xBB) ? 2 : 0));
                        var payloadLength = isMtu ? 2 : (isBurstReq ? 0 : ((length >= 10 && packet[0] == 0xAA && packet[1] == 0xBB) ? length - 2 : length));

                        var isEarly = (wsTxBytes + coalescedLen) < ObxodkaFraming.EarlyStageWindowBytes;
                        var paddingLen = ObxodkaFraming.CalculatePaddingLength(cmd, payloadLength, maxMtu: NetworkDefaults.CurrentMtu, isEarlySession: isEarly);
                        var totalLen = ObxodkaFraming.HeaderSize + payloadLength + paddingLen;

                        if (coalescedLen + totalLen > coalesceBuf.Length && coalescedLen > 0)
                        {
                            await ws.SendAsync(coalesceBuf.AsMemory(0, coalescedLen), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                            wsTxBytes += coalescedLen;
                            coalescedLen = 0;
                        }

                        if (totalLen <= coalesceBuf.Length)
                        {
                            ObxodkaFraming.WriteFrame(coalesceBuf.AsSpan(coalescedLen, totalLen), cmd, packet.AsSpan(payloadOffset, payloadLength), paddingLen, _sessionKey);
                            coalescedLen += totalLen;
                        }
                        else
                        {
                            var frame = ArrayPool<byte>.Shared.Rent(totalLen);
                            try
                            {
                                ObxodkaFraming.WriteFrame(frame.AsSpan(0, totalLen), cmd, packet.AsSpan(payloadOffset, payloadLength), paddingLen, _sessionKey);
                                await ws.SendAsync(frame.AsMemory(0, totalLen), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                                wsTxBytes += totalLen;
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(frame);
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(packet);
                    }
                }

                if (coalescedLen > 0)
                {
                    await ws.SendAsync(coalesceBuf.AsMemory(0, coalescedLen), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                    wsTxBytes += coalescedLen;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Shared.Logging.AppLogger.LogWarning($"[OBXODKA-WS] Writer loop terminated: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(coalesceBuf);
        }
    }

    private async Task PingLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var pingBytes = new byte[8];
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open && Volatile.Read(ref _connected))
        {
            try
            {
                await Task.Delay(5000, ct).ConfigureAwait(false);
                BinaryPrimitives.WriteInt64LittleEndian(pingBytes, Stopwatch.GetTimestamp());
                var frame = ObxodkaFraming.Pack(ObxodkaFraming.CmdPing, pingBytes, out var totalLen, sessionKey: _sessionKey);
                try
                {
                    await ws.SendAsync(frame.AsMemory(0, totalLen), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(frame);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch { }
        }
    }

    public void SendPacketFromPool(byte[] packet, int length)
    {
        if (length <= 0 || !Volatile.Read(ref _connected))
        {
            ArrayPool<byte>.Shared.Return(packet);
            return;
        }

        if (!_txChannel.Writer.TryWrite((packet, length)))
        {
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    public async Task SendDisconnectSignalAsync()
    {
        var ws = _webSocket;
        if (ws is { State: WebSocketState.Open })
        {
            var disc = ObxodkaFraming.Pack(ObxodkaFraming.CmdDisconnect, [], out var totalLen, sessionKey: _sessionKey);
            try
            {
                await ws.SendAsync(disc.AsMemory(0, totalLen), WebSocketMessageType.Binary, true, CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
            finally
            {
                ArrayPool<byte>.Shared.Return(disc);
            }
        }
    }

    public async Task SendPingProbeAsync()
    {
        var ws = _webSocket;
        if (ws is { State: WebSocketState.Open })
        {
            var pingPayload = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(pingPayload, Stopwatch.GetTimestamp());
            var pingFrame = ObxodkaFraming.Pack(ObxodkaFraming.CmdPing, pingPayload, out var totalLen, sessionKey: _sessionKey);
            try
            {
                await ws.SendAsync(pingFrame.AsMemory(0, totalLen), WebSocketMessageType.Binary, true, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pingFrame);
            }
        }
    }

    public Task<bool> ProbeMtuAsync(int size, CancellationToken ct) => Task.FromResult(true);

    public Task<int[]> MeasureBurstMtuAsync(CancellationToken ct)
    {
        lock (_receivedBurstMtus)
        {
            return Task.FromResult<int[]>([.. _receivedBurstMtus]);
        }
    }

    public async Task SendMtuSyncAsync(int mtu)
    {
        var ws = _webSocket;
        if (ws is { State: WebSocketState.Open })
        {
            var buf = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)mtu);
            var frame = ObxodkaFraming.Pack(ObxodkaFraming.CmdMtuSync, buf, out var totalLen, sessionKey: _sessionKey);
            try
            {
                await ws.SendAsync(frame.AsMemory(0, totalLen), WebSocketMessageType.Binary, true, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frame);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Volatile.Write(ref _connected, false);
        _cts?.Cancel();
        _cts?.Dispose();
        _webSocket?.Dispose();
        _wsInvoker?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Volatile.Write(ref _connected, false);
        _cts?.Cancel();
        if (_webSocket is { State: WebSocketState.Open })
        {
            try
            {
                await _webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
        }
        _webSocket?.Dispose();
        _wsInvoker?.Dispose();
        _cts?.Dispose();
    }
}
