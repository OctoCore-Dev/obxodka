using System.Net.WebSockets;

namespace obxodka.Core.Transports;

public sealed class ObxodkaWebSocketTransport(int serverPort = 443, string? configuredSni = null) : IVpnTransport
{
    public string ProtocolName => "OBXODKA-WS";
    public bool IsConnected => Volatile.Read(ref _connected);
    public int EffectivePort { get; private set; } = serverPort;
    public string ActiveSni { get; private set; } = configuredSni ?? "obxodka.one";

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
    private CancellationTokenSource? _cts;
    private bool _connected;
    private bool _disposed;
    private string _activeServerHost = "obxodka.one";
    private volatile string? _assignedIp;
    private readonly List<int> _receivedBurstMtus = [];

    public void ProtectSockets(Action<Socket> protectAction)
    {
        _protectAction = protectAction;
        _ = _protectAction;
    }

    public async Task<(string ip, string ip6)> ConnectAsync(string serverIp, string thumbprint, CancellationToken ct)
    {
        _activeServerHost = !string.IsNullOrWhiteSpace(_configuredSni)
            ? _configuredSni
            : (!IPAddress.TryParse(serverIp, out _) ? serverIp : "obxodka.one");

        ActiveSni = _activeServerHost;
        EffectivePort = _serverPort;

        _cts = new CancellationTokenSource();
        _ipTcs = new TaskCompletionSource<(string ip, string ip6)>(TaskCreationOptions.RunContinuationsAsynchronously);

        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        ws.Options.RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
            CertificateValidator.ValidateServerCertificate(cert, chain, errors, dynamicPinningHash: string.Empty);

        var isDirectIp = IPAddress.TryParse(serverIp, out _);
        var targetHostHeader = isDirectIp ? ActiveSni : serverIp;
        try
        {
            ws.Options.SetRequestHeader("Host", targetHostHeader);
        }
        catch { }
        ws.Options.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");

        var connectHost = _activeServerHost.Contains("obxodka.one", StringComparison.OrdinalIgnoreCase)
            ? _activeServerHost
            : (isDirectIp ? serverIp : _activeServerHost);
        var uriBuilder = new UriBuilder("wss", connectHost, _serverPort, "/api/v1/stream")
        {
            Query = $"ray=0&mtu={NetworkDefaults.CurrentMtu}&v=133.0"
        };

        using var attemptCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token, attemptCts.Token);

        Shared.Logging.AppLogger.Log($"[OBXODKA-WS] Connecting to {uriBuilder.Uri} (SNI={ActiveSni}, Port={_serverPort})...");
        await ws.ConnectAsync(uriBuilder.Uri, linkedCts.Token).ConfigureAwait(false);

        _webSocket = ws;
        Volatile.Write(ref _connected, true);

        var handshakeStr = string.IsNullOrEmpty(_assignedIp)
            ? $"{thumbprint}|R:0|M:{NetworkDefaults.CurrentMtu}"
            : $"{thumbprint}|{_assignedIp}|R:0|M:{NetworkDefaults.CurrentMtu}";
        var hsPayload = Encoding.UTF8.GetBytes(handshakeStr);
        var hsFrame = ObxodkaFraming.Pack(ObxodkaFraming.CmdHandshake, hsPayload, out var hsTotalLen);
        try
        {
            await ws.SendAsync(hsFrame.AsMemory(0, hsTotalLen), WebSocketMessageType.Binary, true, linkedCts.Token).ConfigureAwait(false);
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
            Shared.Logging.AppLogger.Log($"[OBXODKA-WS] Connected! Assigned IPv4: {result.ip}, IPv6: {result.ip6}");
            return result;
        }
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
                    if (!ObxodkaFraming.TryDecodeHeader(headerSpan, out var totalLen, out var payloadLen, out var command, out var salt))
                    {
                        Shared.Logging.AppLogger.LogError("[OBXODKA-WS] Frame header decode error, closing.");
                        return;
                    }

                    if (rxFilled - rxOffset < totalLen)
                    {
                        break;
                    }

                    var payloadSpan = rxBuf.AsSpan(rxOffset + ObxodkaFraming.HeaderSize, payloadLen);
                    ObxodkaFraming.UnmaskPayload(payloadSpan, salt);
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
                                var pongFrame = ObxodkaFraming.Pack(ObxodkaFraming.CmdPong, echoBytes.AsSpan(0, payloadLen), out var pongLen);
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

                        var paddingLen = ObxodkaFraming.CalculatePaddingLength(cmd, payloadLength, maxMtu: NetworkDefaults.CurrentMtu);
                        var totalLen = ObxodkaFraming.HeaderSize + payloadLength + paddingLen;

                        if (coalescedLen + totalLen > coalesceBuf.Length && coalescedLen > 0)
                        {
                            await ws.SendAsync(coalesceBuf.AsMemory(0, coalescedLen), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                            coalescedLen = 0;
                        }

                        if (totalLen <= coalesceBuf.Length)
                        {
                            ObxodkaFraming.WriteFrame(coalesceBuf.AsSpan(coalescedLen, totalLen), cmd, packet.AsSpan(payloadOffset, payloadLength), paddingLen);
                            coalescedLen += totalLen;
                        }
                        else
                        {
                            var frame = ArrayPool<byte>.Shared.Rent(totalLen);
                            try
                            {
                                ObxodkaFraming.WriteFrame(frame.AsSpan(0, totalLen), cmd, packet.AsSpan(payloadOffset, payloadLength), paddingLen);
                                await ws.SendAsync(frame.AsMemory(0, totalLen), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
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
                var frame = ObxodkaFraming.Pack(ObxodkaFraming.CmdPing, pingBytes, out var totalLen);
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
            var disc = ObxodkaFraming.Pack(ObxodkaFraming.CmdDisconnect, [], out var totalLen);
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
            var pingFrame = ObxodkaFraming.Pack(ObxodkaFraming.CmdPing, pingPayload, out var totalLen);
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
            var frame = ObxodkaFraming.Pack(ObxodkaFraming.CmdMtuSync, buf, out var totalLen);
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
        _cts?.Dispose();
    }
}
