namespace obxodka.Core.Transports;

//разделенный транспорт поверх обычного http2/http1 (split-http: потоковый get downlink + короткие post uplink без upgrade websocket)
public sealed class ObxodkaXhttpTransport(int serverPort = 443, string? configuredSni = null, string? preferredAssignedIp = null) : IVpnTransport, IHealthAwareTransport
{
    public string ProtocolName => "OBXODKA-XHTTP";

    public bool IsConnected => Volatile.Read(ref _connected);

    public int EffectivePort { get; private set; } = serverPort;

    public string ActiveSni { get; private set; } = configuredSni ?? AppConfig.PrimaryDomain;

    public TlsBrowserProfile ActiveBrowserProfile { get; private set; } = TlsBrowserProfile.Chrome;

    public string? RemoteConnectedEndpointIp => Volatile.Read(ref _lastConnectedRemoteIp);

    public event Action<byte[], int>? OnPacketReceived;
    public event Action<long>? OnPingUpdated;
    public event Action? OnConnectionDropped;
    public event Action<int>? OnMtuUpdated;

    public static Action<Socket>? OnSocketCreated { get; set; }

    private readonly PriorityPacketQueue _txQueue = new(8192);
    private readonly int _serverPort = serverPort;
    private readonly string? _configuredSni = configuredSni;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<int>> _pendingMtuProbes = new();
    private readonly List<int> _receivedBurstMtus = [];

    private TaskCompletionSource<(string ip, string ip6)> _ipTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action<Socket>? _protectAction;
    private HttpMessageInvoker? _downlinkInvoker;
    private HttpClient? _uplinkClient;
    private CancellationTokenSource? _cts;
    private Task[] _activeLoopTasks = [];
    private volatile string? _assignedIp = preferredAssignedIp;
    private string? _lastConnectedRemoteIp;
    private uint _sessionKey;
    private bool _connected;
    private bool _disposed;
    private int _dropSignaled;

    public void ProtectSockets(Action<Socket> protectAction) => _protectAction = protectAction;

    public void ReportUnhealthyEndpoint()
    {
        var ip = Volatile.Read(ref _lastConnectedRemoteIp);
        if (!string.IsNullOrWhiteSpace(ip))
        {
            EdgeEndpointHealth.MarkUnhealthy(ip, 120);
        }
    }

    //создание http обработчика с обходом dpi, защитой сокетов и ротацией edge ip
    [SuppressMessage("Reliability", "CA2000:Ликвидировать объекты перед потерей области", Justification = "Владение передается в HttpMessageInvoker/HttpClient с disposeHandler: true")]
    private SocketsHttpHandler CreateXhttpHandler(string targetSni, string candidateHost, TlsBrowserProfile profile, bool pinToActiveIp)
    {
        var isDirectIp = IPAddress.TryParse(candidateHost, out var literalIp);
        var isLiteralSni = IPAddress.TryParse(targetSni, out _);
        var pinningHash = isDirectIp
            ? (!string.IsNullOrWhiteSpace(OctopusEngine.DirectOriginCertHash) ? OctopusEngine.DirectOriginCertHash : (OctopusEngine.DynamicSslPublicKeyHash ?? string.Empty))
            : string.Empty;

        return new SocketsHttpHandler
        {
            UseProxy = false,
            Proxy = null,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            EnableMultipleHttp2Connections = true,
            InitialHttp2StreamWindowSize = 8388608,
            SslOptions = SslOptionsFactory.Create(
                targetHost: isLiteralSni ? null : targetSni,
                dynamicPinningHash: pinningHash,
                applicationProtocols: [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
                browserProfile: profile),
            ConnectCallback = async (context, cToken) =>
            {
                IPAddress[] candidateIps;
                if (isDirectIp && literalIp is not null)
                {
                    candidateIps = [literalIp];
                }
                else if (pinToActiveIp &&
                         Volatile.Read(ref _lastConnectedRemoteIp) is { } pinnedIpStr &&
                         IPAddress.TryParse(pinnedIpStr, out var pinnedIp))
                {
                    candidateIps = [pinnedIp];
                }
                else
                {
                    var resolved = await ApiService.ResolveAllHostsAntiPoisonAsync(targetSni, cToken).ConfigureAwait(false);
                    candidateIps = EdgeEndpointHealth.FilterAndRotate(resolved);
                }

                Socket? connectedSocket = null;
                Exception? lastSocketEx = null;

                for (var ipIdx = 0; ipIdx < candidateIps.Length; ipIdx++)
                {
                    var targetIp = candidateIps[ipIdx];
                    var port = context.DnsEndPoint.Port;
                    var socket = new Socket(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true,
                        SendBufferSize = 4194304,
                        ReceiveBufferSize = 4194304
                    };
                    (_protectAction ?? OnSocketCreated ?? OctopusEngine.Current.ProtectSocket).Invoke(socket);

                    try
                    {
                        if (candidateIps.Length > 1 && ipIdx + 1 < candidateIps.Length)
                        {
                            using var perIpCts = CancellationTokenSource.CreateLinkedTokenSource(cToken);
                            perIpCts.CancelAfter(TimeSpan.FromMilliseconds(1600));
                            await socket.ConnectAsync(new IPEndPoint(targetIp, port), perIpCts.Token).ConfigureAwait(false);
                        }
                        else
                        {
                            await socket.ConnectAsync(new IPEndPoint(targetIp, port), cToken).ConfigureAwait(false);
                        }

                        if (!pinToActiveIp)
                        {
                            Volatile.Write(ref _lastConnectedRemoteIp, targetIp.ToString());
                        }
                        connectedSocket = socket;
                        break;
                    }
                    catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or TimeoutException or ObjectDisposedException || cToken.IsCancellationRequested)
                    {
                        lastSocketEx = ex;
                        if (!isDirectIp)
                        {
                            EdgeEndpointHealth.MarkUnhealthy(targetIp.ToString(), 60);
                        }
                        socket.Dispose();
                        if (cToken.IsCancellationRequested)
                        {
                            throw;
                        }
                    }
                }

                if (connectedSocket is null)
                {
                    ApiService.InvalidateHostCache(targetSni);
                    throw lastSocketEx ?? new SocketException((int)SocketError.HostUnreachable);
                }

                var netStream = new NetworkStream(connectedSocket, ownsSocket: true);
                var isLoopback = connectedSocket.RemoteEndPoint is IPEndPoint remoteEp && IPAddress.IsLoopback(remoteEp.Address);
                return !isLoopback
                    ? DpiBypassStream.CreateForProfile(netStream, profile, bypassEnabled: true, forcePureL4: true)
                    : netStream;
            }
        };
    }

    //установка split-http соединения (потоковый get + короткие post на передачу)
    [SuppressMessage("Reliability", "CA2000:Ликвидировать объекты перед потерей области", Justification = "Владение передается _downlinkInvoker/_uplinkClient или гарантированно освобождается в блоке catch")]
    public async Task<(string ip, string ip6)> ConnectAsync(string serverIp, string thumbprint, CancellationToken ct)
    {
        var candidates = SniEngine.BuildXhttpCandidates(serverIp, _serverPort, _configuredSni);
        Exception? lastEx = null;

        for (var cIdx = 0; cIdx < candidates.Count; cIdx++)
        {
            var candidate = candidates[cIdx];
            EffectivePort = candidate.Port;
            ActiveSni = candidate.TargetSni;
            ActiveBrowserProfile = candidate.BrowserProfile;

            Volatile.Write(ref _dropSignaled, 0);
            Volatile.Write(ref _lastConnectedRemoteIp, null);
            _cts = new CancellationTokenSource();
            _ipTcs = new TaskCompletionSource<(string ip, string ip6)>(TaskCreationOptions.RunContinuationsAsynchronously);

            Interlocked.Exchange(ref _downlinkInvoker, null)?.Dispose();
            Interlocked.Exchange(ref _uplinkClient, null)?.Dispose();

            HttpMessageInvoker? downInvoker = null;
            HttpClient? upClient = null;
            HttpResponseMessage? downResp = null;
            var ownershipTransferred = false;

            try
            {
                downInvoker = new HttpMessageInvoker(CreateXhttpHandler(candidate.TargetSni, candidate.ServerHost, candidate.BrowserProfile, pinToActiveIp: false), disposeHandler: true);
                upClient = new HttpClient(CreateXhttpHandler(candidate.TargetSni, candidate.ServerHost, candidate.BrowserProfile, pinToActiveIp: true), disposeHandler: true)
                {
                    Timeout = TimeSpan.FromSeconds(7)
                };

                using var attemptCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(4500));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token, attemptCts.Token);

                var cid = RandomNumberGenerator.GetInt32(10000000, 100000000);
                var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var downSeed = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
                var downKey = ObxodkaFraming.DeriveSessionKey(thumbprint, 0, downSeed);
                var downProof = ObxodkaFraming.ComputeHandshakeProof(thumbprint, 0, downSeed, nowSec);
                var hsStr = string.IsNullOrEmpty(_assignedIp)
                    ? $"{thumbprint}|R:0|M:{SmartMtuEngine.CurrentMtu}|K:{downSeed:X8}|T:{nowSec}|P:{downProof}"
                    : $"{thumbprint}|{_assignedIp}|R:0|M:{SmartMtuEngine.CurrentMtu}|K:{downSeed:X8}|T:{nowSec}|P:{downProof}";

                var uriBuilder = new UriBuilder("https", candidate.ConnectHost, candidate.Port, candidate.Path)
                {
                    Query = $"cid={cid}&_t={nowSec}&_x=1"
                };

                using var req = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri)
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                };
                RoutingHello.ApplyBrowserHeaders(req, candidate.BrowserProfile);
                req.Headers.Host = candidate.HostHeader;
                _ = req.Headers.TryAddWithoutValidation("X-Obx-Mode", "down");
                _ = req.Headers.TryAddWithoutValidation("X-Obx-Handshake", hsStr);
                _ = req.Headers.TryAddWithoutValidation("Cache-Control", "no-store");

                AppLogger.Log($"[OBXODKA-XHTTP] Connecting downlink GET to {uriBuilder.Uri} (SNI={ActiveSni}, Profile={ActiveBrowserProfile})...");
                downResp = await downInvoker.SendAsync(req, linkedCts.Token).ConfigureAwait(false);
                var contentType = downResp.Content.Headers.ContentType?.MediaType;
                if (!downResp.IsSuccessStatusCode || !string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Endpoint returned HTTP {(int)downResp.StatusCode} ({contentType ?? "unknown"}) instead of XHTTP stream.");
                }

                var downStream = await downResp.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);

                _downlinkInvoker = downInvoker;
                _uplinkClient = upClient;
                Volatile.Write(ref _sessionKey, downKey);
                ownershipTransferred = true;

                var readerTask = Task.Run(() => ReaderLoopAsync(downResp, downStream, downKey, _cts.Token), _cts.Token);
                var writerTask = Task.Run(() => WriterLoopAsync(upClient, uriBuilder.Uri, candidate.HostHeader, candidate.BrowserProfile, thumbprint, _cts.Token), _cts.Token);
                _activeLoopTasks = [readerTask, writerTask];

                using (linkedCts.Token.Register(() => _ = _ipTcs.TrySetCanceled()))
                {
                    var result = await _ipTcs.Task.ConfigureAwait(false);
                    Volatile.Write(ref _connected, true);
                    var pingTask = Task.Run(() => PingLoopAsync(_cts.Token), _cts.Token);
                    _activeLoopTasks = [readerTask, writerTask, pingTask];
                    AppLogger.Log($"[OBXODKA-XHTTP] Connected via '{candidate.ServerHost}:{candidate.Port}' (Edge={_lastConnectedRemoteIp ?? "n/a"}, Profile={ActiveBrowserProfile})! Assigned IPv4={result.ip}");
                    return result;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && cIdx + 1 < candidates.Count)
            {
                lastEx = ex;
                AppLogger.LogWarning($"[OBXODKA-XHTTP] Candidate #{cIdx + 1}/{candidates.Count} '{candidate.ServerHost}:{candidate.Port}' failed: {ex.Message}");
                ReportUnhealthyEndpoint();
                await CleanupFailedAttemptAsync(downResp, upClient, downInvoker, ownershipTransferred).ConfigureAwait(false);
            }
            catch
            {
                ReportUnhealthyEndpoint();
                await CleanupFailedAttemptAsync(downResp, upClient, downInvoker, ownershipTransferred).ConfigureAwait(false);
                throw;
            }
        }

        throw lastEx ?? new TimeoutException("XHTTP connection failed across all candidates.");
    }

    //освобождение ресурсов и дренирование пула при неудачной попытке подключения xhttp
    private async Task CleanupFailedAttemptAsync(HttpResponseMessage? downResp, HttpClient? upClient, HttpMessageInvoker? downInvoker, bool ownershipTransferred)
    {
        Volatile.Write(ref _connected, false);
        using var attemptTokenSource = Interlocked.Exchange(ref _cts, null);
        if (attemptTokenSource is not null)
        {
            await attemptTokenSource.CancelAsync().ConfigureAwait(false);
        }

        var loopTasks = _activeLoopTasks;
        _activeLoopTasks = [];
        if (loopTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(loopTasks).WaitAsync(TimeSpan.FromMilliseconds(800), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or HttpRequestException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                //подавление ожидаемых ошибок остановки фоновых задач
            }
        }

        _txQueue.DrainAndReturn(static buf => ArrayPool<byte>.Shared.Return(buf));
        if (!ownershipTransferred)
        {
            downResp?.Dispose();
            upClient?.Dispose();
            downInvoker?.Dispose();
        }
        else
        {
            downResp?.Dispose();
        }

        Interlocked.Exchange(ref _downlinkInvoker, null)?.Dispose();
        Interlocked.Exchange(ref _uplinkClient, null)?.Dispose();
    }

    private async Task ReaderLoopAsync(HttpResponseMessage response, Stream stream, uint sessionKey, CancellationToken ct)
    {
        var rxBuf = ArrayPool<byte>.Shared.Rent(131072);
        var rxFilled = 0;
        var rxOffset = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var readCount = await stream.ReadAsync(rxBuf.AsMemory(rxFilled, rxBuf.Length - rxFilled), ct).ConfigureAwait(false);
                if (readCount <= 0)
                {
                    break;
                }

                rxFilled += readCount;

                while (rxFilled - rxOffset >= ObxodkaFraming.HeaderSize)
                {
                    var headerSpan = rxBuf.AsSpan(rxOffset, ObxodkaFraming.HeaderSize);
                    if (!ObxodkaFraming.TryDecodeHeader(headerSpan, out var totalLen, out var payloadLen, out var command, out var salt, sessionKey))
                    {
                        AppLogger.LogError("[OBXODKA-XHTTP] Frame header decode error, closing.");
                        return;
                    }

                    if (rxFilled - rxOffset < totalLen)
                    {
                        break;
                    }

                    var payloadSpan = rxBuf.AsSpan(rxOffset + ObxodkaFraming.HeaderSize, payloadLen);
                    if (command != ObxodkaFraming.CmdChaff && payloadLen > 0)
                    {
                        ObxodkaFraming.UnmaskPayload(payloadSpan, salt, sessionKey);
                    }
                    rxOffset += totalLen;

                    HandleDecodedDownlinkFrame(command, payloadSpan, payloadLen);
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
        catch (Exception ex) when (ex is IOException or SocketException or HttpRequestException or ObjectDisposedException or InvalidOperationException)
        {
            AppLogger.LogWarning($"[OBXODKA-XHTTP] Downlink reader stopped: {ex.Message}");
        }
        finally
        {
            response.Dispose();
            ArrayPool<byte>.Shared.Return(rxBuf);
            if (!_ipTcs.Task.IsCompleted)
            {
                _ = _ipTcs.TrySetCanceled(ct.IsCancellationRequested ? ct : CancellationToken.None);
            }
            NotifyDropOnce();
        }
    }

    private void HandleDecodedDownlinkFrame(byte command, ReadOnlySpan<byte> payloadSpan, int payloadLen)
    {
        if (command == ObxodkaFraming.CmdHandshakeResponse)
        {
            var info = Encoding.UTF8.GetString(payloadSpan);
            if (info.StartsWith("IP:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = info[3..].Split('|');
                var assignedIp = parts[0];
                var assignedIpV6 = parts.Length > 1 ? parts[1] : string.Empty;
                for (var pIdx = 0; pIdx < parts.Length; pIdx++)
                {
                    var part = parts[pIdx];
                    if (part.StartsWith("MTU:", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(part[4..], out var sMtu) &&
                        sMtu is >= SmartMtuEngine.MinMtu and <= SmartMtuEngine.MaxMtu)
                    {
                        SmartMtuEngine.CurrentMtu = sMtu;
                    }
                }
                _assignedIp = assignedIp;
                _ = _ipTcs.TrySetResult((assignedIp, assignedIpV6));
            }
            return;
        }

        if (command == ObxodkaFraming.CmdData)
        {
            if (OnPacketReceived is { } onPacketReceived)
            {
                var packetBuf = ArrayPool<byte>.Shared.Rent(payloadLen);
                payloadSpan.CopyTo(packetBuf);
                onPacketReceived(packetBuf, payloadLen);
            }
            return;
        }

        if (command == ObxodkaFraming.CmdPing)
        {
            if (payloadLen >= 8)
            {
                var echoLen = 2 + payloadLen;
                var echoBytes = ArrayPool<byte>.Shared.Rent(echoLen);
                echoBytes[0] = 0xEE;
                echoBytes[1] = 0x88;
                payloadSpan.CopyTo(echoBytes.AsSpan(2, payloadLen));
                if (!_txQueue.TryEnqueue(echoBytes, echoLen))
                {
                    ArrayPool<byte>.Shared.Return(echoBytes);
                }
            }
            return;
        }

        if (command == ObxodkaFraming.CmdPong)
        {
            var tsSpan = (payloadLen >= 10 && payloadSpan[0] == 0xAA && payloadSpan[1] == 0xBB)
                ? payloadSpan.Slice(2, 8)
                : (payloadLen >= 8 ? payloadSpan[..8] : default);

            if (!tsSpan.IsEmpty)
            {
                var sentTicks = BinaryPrimitives.ReadInt64LittleEndian(tsSpan);
                var rtt = (Stopwatch.GetTimestamp() - sentTicks) * 1000 / Stopwatch.Frequency;
                if (rtt is >= 0 and < 5000)
                {
                    OnPingUpdated?.Invoke(rtt);
                }

                if (_pendingMtuProbes.TryRemove(sentTicks, out var probeTcs))
                {
                    _ = probeTcs.TrySetResult(payloadLen);
                }
            }
            return;
        }

        if (command == ObxodkaFraming.CmdMtuSync && payloadLen >= 2)
        {
            var sMtu = BinaryPrimitives.ReadUInt16BigEndian(payloadSpan);
            if (sMtu is >= SmartMtuEngine.MinMtu and <= SmartMtuEngine.MaxMtu)
            {
                SmartMtuEngine.CurrentMtu = sMtu;
                OnMtuUpdated?.Invoke(sMtu);
            }
            return;
        }

        if (command == ObxodkaFraming.CmdMtuBurstPayload &&
            payloadLen >= 4 &&
            payloadSpan[0] == 0xEE &&
            payloadSpan[1] == 0x55)
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

    private async Task WriterLoopAsync(
        HttpClient upClient,
        Uri baseEndpoint,
        string hostHeader,
        TlsBrowserProfile profile,
        string thumbprint,
        CancellationToken ct)
    {
        var coalesceBuf = ArrayPool<byte>.Shared.Rent(131072);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var firstItem = await _txQueue.DequeueAsync(ct).ConfigureAwait(false);
                var coalescedLen = 0;
                var batchCount = 0;
                var item = firstItem;
                var hasItem = true;

                var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var postSeed = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
                var postKey = ObxodkaFraming.DeriveSessionKey(thumbprint, 0, postSeed);
                var postProof = ObxodkaFraming.ComputeHandshakeProof(thumbprint, 0, postSeed, nowSec);
                var hsStr = $"{thumbprint}|R:0|M:{SmartMtuEngine.CurrentMtu}|K:{postSeed:X8}|T:{nowSec}|P:{postProof}";

                while (hasItem)
                {
                    var packet = item.buffer;
                    var length = item.length;

                    if (packet is not null && length > 0)
                    {
                        try
                        {
                            var cmd = TransportFrameControlMapper.ClassifyOutgoingPacket(packet, length, out var payloadOffset, out var payloadLength);
                            var paddingLen = ObxodkaFraming.CalculatePaddingLength(cmd, payloadLength, maxMtu: SmartMtuEngine.CurrentMtu, isEarlySession: false);
                            var totalLen = ObxodkaFraming.HeaderSize + payloadLength + paddingLen;

                            if (coalescedLen + totalLen > coalesceBuf.Length && coalescedLen > 0)
                            {
                                await PostBatchAsync(upClient, baseEndpoint, hostHeader, profile, hsStr, coalesceBuf.AsMemory(0, coalescedLen), ct).ConfigureAwait(false);
                                coalescedLen = 0;
                                postSeed = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
                                postKey = ObxodkaFraming.DeriveSessionKey(thumbprint, 0, postSeed);
                                postProof = ObxodkaFraming.ComputeHandshakeProof(thumbprint, 0, postSeed, nowSec);
                                hsStr = $"{thumbprint}|R:0|M:{SmartMtuEngine.CurrentMtu}|K:{postSeed:X8}|T:{nowSec}|P:{postProof}";
                            }

                            var payloadSpan = payloadLength > 0 ? packet.AsSpan(payloadOffset, payloadLength) : ReadOnlySpan<byte>.Empty;
                            if (totalLen <= coalesceBuf.Length)
                            {
                                ObxodkaFraming.WriteFrame(coalesceBuf.AsSpan(coalescedLen, totalLen), cmd, payloadSpan, paddingLen, postKey);
                                coalescedLen += totalLen;
                            }
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(packet);
                        }
                    }

                    batchCount++;
                    hasItem = batchCount < 64 && _txQueue.TryDequeue(out item);
                }

                if (coalescedLen > 0)
                {
                    await PostBatchAsync(upClient, baseEndpoint, hostHeader, profile, hsStr, coalesceBuf.AsMemory(0, coalescedLen), ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or SocketException or HttpRequestException or ObjectDisposedException or InvalidOperationException)
        {
            AppLogger.LogWarning($"[OBXODKA-XHTTP] Uplink writer stopped: {ex.Message}");
            NotifyDropOnce();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(coalesceBuf);
        }
    }

    private static async Task PostBatchAsync(
        HttpClient upClient,
        Uri baseEndpoint,
        string hostHeader,
        TlsBrowserProfile profile,
        string hsHeader,
        ReadOnlyMemory<byte> body,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, baseEndpoint)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = new ReadOnlyMemoryContent(body)
        };
        RoutingHello.ApplyBrowserHeaders(req, profile);
        req.Headers.Host = hostHeader;
        _ = req.Headers.TryAddWithoutValidation("X-Obx-Mode", "up");
        _ = req.Headers.TryAddWithoutValidation("X-Obx-Handshake", hsHeader);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var resp = await upClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"XHTTP uplink rejected with HTTP {(int)resp.StatusCode}", null, resp.StatusCode);
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Volatile.Read(ref _connected))
        {
            try
            {
                await Task.Delay(5000, ct).ConfigureAwait(false);
                await SendPingProbeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or ObjectDisposedException or InvalidOperationException) { }
        }
    }

    private void NotifyDropOnce()
    {
        if (Volatile.Read(ref _connected) && Interlocked.Exchange(ref _dropSignaled, 1) == 0)
        {
            Volatile.Write(ref _connected, false);
            OnConnectionDropped?.Invoke();
        }
    }

    public void SendPacketFromPool(byte[] packet, int length)
    {
        if (length <= 0 || !Volatile.Read(ref _connected))
        {
            ArrayPool<byte>.Shared.Return(packet);
            return;
        }

        if (!_txQueue.TryEnqueue(packet, length))
        {
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    public Task SendDisconnectSignalAsync()
    {
        if (!Volatile.Read(ref _connected))
        {
            return Task.CompletedTask;
        }

        var discBuf = ArrayPool<byte>.Shared.Rent(2);
        discBuf[0] = 0xEE;
        discBuf[1] = 0x99;
        if (!_txQueue.TryEnqueue(discBuf, 2))
        {
            ArrayPool<byte>.Shared.Return(discBuf);
        }
        return Task.CompletedTask;
    }

    public Task SendPingProbeAsync()
    {
        if (!Volatile.Read(ref _connected))
        {
            return Task.CompletedTask;
        }

        var pingPayload = ArrayPool<byte>.Shared.Rent(8);
        BinaryPrimitives.WriteInt64LittleEndian(pingPayload.AsSpan(0, 8), Stopwatch.GetTimestamp());
        if (!_txQueue.TryEnqueue(pingPayload, 8))
        {
            ArrayPool<byte>.Shared.Return(pingPayload);
        }
        return Task.CompletedTask;
    }

    public async Task<bool> ProbeMtuAsync(int size, CancellationToken ct)
    {
        if (!Volatile.Read(ref _connected) || _cts?.IsCancellationRequested == true || size < 16)
        {
            return false;
        }

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeTs = Stopwatch.GetTimestamp();
        while (!_pendingMtuProbes.TryAdd(probeTs, tcs))
        {
            probeTs++;
        }

        var payloadLen = Math.Max(16, size - 2);
        var queueLen = payloadLen + 2;
        var buf = ArrayPool<byte>.Shared.Rent(queueLen);
        try
        {
            buf[0] = 0xAA;
            buf[1] = 0xBB;
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(2, 8), probeTs);
            buf[10] = 0x11;
            RandomNumberGenerator.Fill(buf.AsSpan(11, queueLen - 11));

            if (!_txQueue.TryEnqueue(buf, queueLen))
            {
                ArrayPool<byte>.Shared.Return(buf);
                return false;
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(TimeSpan.FromMilliseconds(1200));
            using var reg = linkedCts.Token.Register(() => _ = tcs.TrySetResult(0));

            var returnedLen = await tcs.Task.ConfigureAwait(false);
            return returnedLen >= payloadLen;
        }
        finally
        {
            _ = _pendingMtuProbes.TryRemove(probeTs, out _);
        }
    }

    public Task SendMtuSyncAsync(int mtu)
    {
        if (!Volatile.Read(ref _connected))
        {
            return Task.CompletedTask;
        }

        var buf = ArrayPool<byte>.Shared.Rent(4);
        buf[0] = 0xCC;
        buf[1] = 0xDD;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), (ushort)mtu);
        if (!_txQueue.TryEnqueue(buf, 4))
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Volatile.Write(ref _connected, false);
        _txQueue.Dispose();
        _txQueue.DrainAndReturn(static buf => ArrayPool<byte>.Shared.Return(buf));
        _cts?.Cancel();
        _cts?.Dispose();
        _uplinkClient?.Dispose();
        _downlinkInvoker?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Volatile.Write(ref _connected, false);
        using var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        var loopTasks = _activeLoopTasks;
        _activeLoopTasks = [];
        if (loopTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(loopTasks).WaitAsync(TimeSpan.FromSeconds(1.2), CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
        }

        _txQueue.Dispose();
        _txQueue.DrainAndReturn(static buf => ArrayPool<byte>.Shared.Return(buf));
        _uplinkClient?.Dispose();
        _downlinkInvoker?.Dispose();
    }
}
