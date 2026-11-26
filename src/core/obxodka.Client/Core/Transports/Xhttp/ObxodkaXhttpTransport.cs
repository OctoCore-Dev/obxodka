namespace obxodka.Core.Transports;

//разделенный транспорт поверх обычного http2/http1 (split-http: потоковый get downlink + короткие post uplink без upgrade websocket)
public sealed class ObxodkaXhttpTransport(int serverPort = 443, string? configuredSni = null, string? preferredAssignedIp = null)
    : IVpnTransport, IHealthAwareTransport
{
    private readonly string? _configuredSni = configuredSni;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<int>> _pendingMtuProbes = new();
    private readonly List<int> _receivedBurstMtus = [];
    private readonly int _serverPort = serverPort;

    private readonly PriorityPacketQueue _txQueue = new(8192);
    private Task[] _activeLoopTasks = [];
    private volatile string? _assignedIp = preferredAssignedIp;
    private bool _connected;
    private CancellationTokenSource? _cts;
    private int _disposed;
    private HttpMessageInvoker? _downlinkInvoker;
    private int _dropSignaled;

    private TaskCompletionSource<(string ip, string ip6)> _ipTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _lastConnectedRemoteIp;
    private Action<Socket>? _protectAction;
    private uint _sessionKey;
    private HttpClient? _uplinkClient;

    private string ActiveSni { get; set; } = configuredSni ?? AppConfig.PrimaryDomain;

    private TlsBrowserProfile ActiveBrowserProfile { get; set; } = TlsBrowserProfile.Chrome;

    private static Action<Socket>? OnSocketCreated => null;

    public string? RemoteConnectedEndpointIp => Volatile.Read(ref _lastConnectedRemoteIp);

    public void ReportUnhealthyEndpoint()
    {
        var ip = Volatile.Read(ref _lastConnectedRemoteIp);
        if (!string.IsNullOrWhiteSpace(ip))
        {
            EdgeEndpointHealth.MarkUnhealthy(ip);
        }
    }

    public string ProtocolName => "OBXODKA-XHTTP";

    public bool IsConnected => Volatile.Read(ref _connected);

    public event Action<byte[], int>? OnPacketReceived;
    public event Action<long>? OnPingUpdated;
    public event Action? OnConnectionDropped;
    public event Action<int>? OnMtuUpdated;

    public void ProtectSockets(Action<Socket> protectAction) => _protectAction = protectAction;

    //установка split-http соединения (потоковый get + короткие post на передачу)
    [SuppressMessage("Reliability", "CA2000:Ликвидировать объекты перед потерей области",
        Justification = "Владение передается _downlinkInvoker/_uplinkClient или гарантированно освобождается в блоке catch")]
    public async Task<(string ip, string ip6)> ConnectAsync(string serverIp, string thumbprint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var candidates = SniEngine.BuildXhttpCandidates(serverIp, _serverPort, _configuredSni);
        Exception? lastEx = null;

        for (var cIdx = 0; cIdx < candidates.Count; cIdx++)
        {
            var candidate = candidates[cIdx];
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
                downInvoker = new HttpMessageInvoker(
                    CreateXhttpHandler(candidate.TargetSni, candidate.ServerHost, candidate.BrowserProfile, false), true);
                upClient = new HttpClient(CreateXhttpHandler(candidate.TargetSni, candidate.ServerHost, candidate.BrowserProfile, true),
                    true)
                {
                    Timeout = TimeSpan.FromSeconds(7)
                };

                using var attemptCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(4500));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token, attemptCts.Token);

                var cid = RandomNumberGenerator.GetInt32(10000000, 100000000);
                var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var downSeed = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
                var downKey = ObxodkaFraming.DeriveSessionKey(thumbprint, 0, downSeed);
                var hsStr = OctopusEngine.Current.BuildAuthenticatedHandshakeString(
                    thumbprint,
                    _assignedIp,
                    0,
                    SmartMtuEngine.CurrentMtu,
                    downSeed,
                    nowSec,
                    includePublicKey: true);

                var uriBuilder = new UriBuilder("https", candidate.ConnectHost, candidate.Port, candidate.Path)
                {
                    Query = $"v={cid}"
                };

                using var req = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri);
                req.Version = HttpVersion.Version20;
                req.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
                RoutingHello.ApplyBrowserHeaders(req, candidate.BrowserProfile);
                req.Headers.Host = candidate.HostHeader;
                _ = req.Headers.TryAddWithoutValidation("Cookie", ObxodkaFraming.PackStealthCookieHeader("down", hsStr, nowSec));
                _ = req.Headers.TryAddWithoutValidation("Cache-Control", "no-store");

                AppLogger.Log(
                    $"[OBXODKA-XHTTP] Connecting downlink GET to {uriBuilder.Uri} (SNI={ActiveSni}, Profile={ActiveBrowserProfile})...");
                downResp = await downInvoker.SendAsync(req, linkedCts.Token).ConfigureAwait(false);
                var contentType = downResp.Content.Headers.ContentType?.MediaType;
                var validStreamType = string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(contentType, "application/x-protobuf", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(contentType, "video/mp4", StringComparison.OrdinalIgnoreCase);
                if (!downResp.IsSuccessStatusCode || !validStreamType)
                {
                    throw new InvalidOperationException(
                        $"Endpoint returned HTTP {(int)downResp.StatusCode} ({contentType ?? "unknown"}) instead of XHTTP stream.");
                }

                var downStream = await downResp.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);

                _downlinkInvoker = downInvoker;
                _uplinkClient = upClient;
                Volatile.Write(ref _sessionKey, downKey);
                ownershipTransferred = true;

                var readerTask = Task.Run(() => ReaderLoopAsync(downResp, downStream, downKey, _cts.Token), _cts.Token);
                var writerTask =
                    Task.Run(
                        () => WriterLoopAsync(upClient, uriBuilder.Uri, candidate.HostHeader, candidate.BrowserProfile, thumbprint,
                            _cts.Token), _cts.Token);
                _activeLoopTasks = [readerTask, writerTask];

                await using (linkedCts.Token.Register(() => _ = _ipTcs.TrySetCanceled()))
                {
                    var result = await _ipTcs.Task.ConfigureAwait(false);
                    Volatile.Write(ref _connected, true);
                    var pingTask = Task.Run(() => PingLoopAsync(_cts.Token), _cts.Token);
                    _activeLoopTasks = [readerTask, writerTask, pingTask];
                    AppLogger.Log(
                        $"[OBXODKA-XHTTP] Connected via '{candidate.ServerHost}:{candidate.Port}' (Edge={_lastConnectedRemoteIp ?? "n/a"}, Profile={ActiveBrowserProfile})! Assigned IPv4={result.ip}");
                    return result;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && cIdx + 1 < candidates.Count)
            {
                lastEx = ex;
                AppLogger.LogWarning(
                    $"[OBXODKA-XHTTP] Candidate #{cIdx + 1}/{candidates.Count} '{candidate.ServerHost}:{candidate.Port}' failed: {ex.Message}");
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
            _ = ((IDictionary<long, TaskCompletionSource<int>>)_pendingMtuProbes).Remove(probeTs);
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

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
            catch
            {
                // ignored
            }
        }

        _txQueue.Dispose();
        _txQueue.DrainAndReturn(static buf => ArrayPool<byte>.Shared.Return(buf));
        _uplinkClient?.Dispose();
        _downlinkInvoker?.Dispose();
    }

    //создание http обработчика с обходом dpi, защитой сокетов и ротацией edge ip
    [SuppressMessage("Reliability", "CA2000:Ликвидировать объекты перед потерей области",
        Justification = "Владение передается в HttpMessageInvoker/HttpClient с disposeHandler: true")]
    private SocketsHttpHandler CreateXhttpHandler(string targetSni, string candidateHost, TlsBrowserProfile profile, bool pinToActiveIp)
    {
        var isDirectIp = IPAddress.TryParse(candidateHost, out var literalIp);
        var isLiteralSni = IPAddress.TryParse(targetSni, out _);
        var pinningHash = isDirectIp
            ? !string.IsNullOrWhiteSpace(OctopusEngine.DirectOriginCertHash)
                ? OctopusEngine.DirectOriginCertHash
                : OctopusEngine.DynamicSslPublicKeyHash ?? string.Empty
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
                isLiteralSni ? null : targetSni,
                pinningHash,
                [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
                profile),
            ConnectCallback = async (context, cToken) =>
            {
                var candidateIps = isDirectIp && literalIp is not null
                    ? [literalIp]
                    : pinToActiveIp &&
                      Volatile.Read(ref _lastConnectedRemoteIp) is { } pinnedIpStr &&
                      IPAddress.TryParse(pinnedIpStr, out var pinnedIp)
                        ? [pinnedIp]
                        : await CleanEdgeScanner.ResolveCleanShieldCandidatesAsync(
                                targetSni,
                                context.DnsEndPoint.Port,
                                cToken)
                            .ConfigureAwait(false);

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
                    catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or TimeoutException
                                                   or ObjectDisposedException || cToken.IsCancellationRequested)
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

                var netStream = new NetworkStream(connectedSocket, true);
                var isLoopback = connectedSocket.RemoteEndPoint is IPEndPoint remoteEp && IPAddress.IsLoopback(remoteEp.Address);
                return !isLoopback
                    ? DpiBypassStream.CreateForProfile(netStream, profile, true, true)
                    : netStream;
            }
        };
    }

    //освобождение ресурсов и дренирование пула при неудачной попытке подключения xhttp
    private async Task CleanupFailedAttemptAsync(HttpResponseMessage? downResp, HttpClient? upClient, HttpMessageInvoker? downInvoker,
        bool ownershipTransferred)
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
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or HttpRequestException or IOException
                                           or ObjectDisposedException or InvalidOperationException)
            {
                //подавление ожидаемых ошибок остановки фоновых задач
            }
        }

        _txQueue.DrainAndReturn(static buf => ArrayPool<byte>.Shared.Return(buf));
        downResp?.Dispose();
        if (!ownershipTransferred)
        {
            upClient?.Dispose();
            downInvoker?.Dispose();
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
            using (response)
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
                        if (!ObxodkaFraming.TryDecodeHeader(headerSpan, out var totalLen, out var payloadLen, out var command, out var salt,
                                sessionKey))
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

                    if (rxOffset <= 0)
                    {
                        continue;
                    }

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
        catch (OperationCanceledException)
        {
            //штатная остановка цикла чтения downlink
        }
        catch (Exception ex) when (ex is IOException or SocketException or HttpRequestException or ObjectDisposedException
                                       or InvalidOperationException)
        {
            AppLogger.LogWarning($"[OBXODKA-XHTTP] Downlink reader stopped: {ex.Message}");
        }
        finally
        {
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
        switch (command)
        {
            case ObxodkaFraming.CmdHandshakeResponse:
                {
                    var info = Encoding.UTF8.GetString(payloadSpan);
                    if (!info.StartsWith("IP:", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    var parts = info[3..].Split('|');
                    var assignedIp = parts[0];
                    var assignedIpV6 = parts.Length > 1 ? parts[1] : string.Empty;
                    foreach (var part in parts.Where(static part => part.StartsWith("MTU:", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (int.TryParse(part[4..], out var sMtu) &&
                            sMtu is >= SmartMtuEngine.MinMtu and <= SmartMtuEngine.MaxMtu)
                        {
                            OnMtuUpdated?.Invoke(sMtu);
                        }
                    }

                    _assignedIp = assignedIp;
                    _ = _ipTcs.TrySetResult((assignedIp, assignedIpV6));

                    return;
                }
            case ObxodkaFraming.CmdData:
                {
                    if (OnPacketReceived is not { } onPacketReceived)
                    {
                        return;
                    }

                    var packetBuf = ArrayPool<byte>.Shared.Rent(payloadLen);
                    payloadSpan.CopyTo(packetBuf);
                    onPacketReceived(packetBuf, payloadLen);

                    return;
                }
            case ObxodkaFraming.CmdPing:
                {
                    if (payloadLen < 8)
                    {
                        return;
                    }

                    var echoLen = 2 + payloadLen;
                    var echoBytes = ArrayPool<byte>.Shared.Rent(echoLen);
                    echoBytes[0] = 0xEE;
                    echoBytes[1] = 0x88;
                    payloadSpan.CopyTo(echoBytes.AsSpan(2, payloadLen));
                    if (!_txQueue.TryEnqueue(echoBytes, echoLen))
                    {
                        ArrayPool<byte>.Shared.Return(echoBytes);
                    }

                    return;
                }
            case ObxodkaFraming.CmdPong:
                {
                    var tsSpan = payloadLen >= 10 && payloadSpan[0] == 0xAA && payloadSpan[1] == 0xBB
                        ? payloadSpan.Slice(2, 8)
                        : payloadLen >= 8
                            ? payloadSpan[..8]
                            : default;

                    if (tsSpan.IsEmpty)
                    {
                        return;
                    }

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

                    return;
                }
            case ObxodkaFraming.CmdMtuSync when payloadLen >= 2:
                {
                    var sMtu = BinaryPrimitives.ReadUInt16BigEndian(payloadSpan);
                    if (sMtu is < SmartMtuEngine.MinMtu or > SmartMtuEngine.MaxMtu)
                    {
                        return;
                    }

                    OnMtuUpdated?.Invoke(sMtu);

                    return;
                }
            case ObxodkaFraming.CmdMtuBurstPayload when
                payloadLen >= 4 &&
                payloadSpan[0] == 0xEE &&
                payloadSpan[1] == 0x55:
                {
                    var targetMtu = BinaryPrimitives.ReadUInt16BigEndian(payloadSpan.Slice(2, 2));
                    lock (_receivedBurstMtus)
                    {
                        if (!_receivedBurstMtus.Contains(targetMtu))
                        {
                            _receivedBurstMtus.Add(targetMtu);
                        }
                    }

                    break;
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
        const int maxConcurrentPosts = 3;
        const int batchBufferSize = 131072;
        using var inflightGate = new SemaphoreSlim(maxConcurrentPosts, maxConcurrentPosts);
        var inflightTasks = new List<Task>(maxConcurrentPosts * 2);
        long uplinkTxBytes = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var firstItem = await _txQueue.DequeueAsync(ct).ConfigureAwait(false);
                if (firstItem.length <= 0)
                {
                    break;
                }

                var coalesceBuf = ArrayPool<byte>.Shared.Rent(batchBufferSize);
                var ownershipPassed = false;

                try
                {
                    var coalescedLen = 0;
                    var batchCount = 0;
                    var item = firstItem;
                    var hasItem = true;

                    var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var postSeed = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
                    var postKey = ObxodkaFraming.DeriveSessionKey(thumbprint, 0, postSeed);
                    var hsStr = OctopusEngine.Current.BuildAuthenticatedHandshakeString(
                        thumbprint,
                        null,
                        0,
                        SmartMtuEngine.CurrentMtu,
                        postSeed,
                        nowSec);

                    while (hasItem)
                    {
                        var packet = item.buffer;
                        var length = item.length;

                        if (packet is not null && length > 0)
                        {
                            try
                            {
                                var cmd = TransportFrameControlMapper.ClassifyOutgoingPacket(packet, length, out var payloadOffset,
                                    out var payloadLength);
                                var payloadSpan = payloadLength > 0 ? packet.AsSpan(payloadOffset, payloadLength) : ReadOnlySpan<byte>.Empty;
                                var isEarly = Interlocked.Read(ref uplinkTxBytes) + coalescedLen < ObxodkaFraming.EarlyStageWindowBytes;
                                var paddingLen = InnerTlsMorpher.CalculateAntiMlPadding(payloadSpan, cmd, SmartMtuEngine.CurrentMtu, isEarly);
                                var totalLen = ObxodkaFraming.HeaderSize + payloadLength + paddingLen;

                                if (coalescedLen + totalLen > coalesceBuf.Length && coalescedLen > 0)
                                {
                                    await inflightGate.WaitAsync(ct).ConfigureAwait(false);
                                    _ = Interlocked.Add(ref uplinkTxBytes, coalescedLen);
                                    var dispatchedBuf = coalesceBuf;
                                    var dispatchedLen = coalescedLen;
                                    var dispatchedHs = hsStr;
                                    var dispatchedTs = nowSec;
                                    _ = inflightTasks.RemoveAll(static t => t.IsCompleted);
                                    inflightTasks.Add(DispatchPostBatchAsync(
                                        upClient,
                                        baseEndpoint,
                                        hostHeader,
                                        profile,
                                        dispatchedHs,
                                        dispatchedTs,
                                        dispatchedBuf,
                                        dispatchedLen,
                                        inflightGate,
                                        ct));

                                    coalesceBuf = ArrayPool<byte>.Shared.Rent(batchBufferSize);
                                    coalescedLen = 0;
                                    nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                                    postSeed = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
                                    postKey = ObxodkaFraming.DeriveSessionKey(thumbprint, 0, postSeed);
                                    hsStr = OctopusEngine.Current.BuildAuthenticatedHandshakeString(
                                        thumbprint,
                                        null,
                                        0,
                                        SmartMtuEngine.CurrentMtu,
                                        postSeed,
                                        nowSec);
                                }

                                if (totalLen <= coalesceBuf.Length)
                                {
                                    ObxodkaFraming.WriteFrame(coalesceBuf.AsSpan(coalescedLen, totalLen), cmd, payloadSpan, paddingLen,
                                        postKey);
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
                        await inflightGate.WaitAsync(ct).ConfigureAwait(false);
                        _ = Interlocked.Add(ref uplinkTxBytes, coalescedLen);
                        ownershipPassed = true;
                        _ = inflightTasks.RemoveAll(static t => t.IsCompleted);
                        inflightTasks.Add(DispatchPostBatchAsync(
                            upClient,
                            baseEndpoint,
                            hostHeader,
                            profile,
                            hsStr,
                            nowSec,
                            coalesceBuf,
                            coalescedLen,
                            inflightGate,
                            ct));
                    }
                }
                finally
                {
                    if (!ownershipPassed)
                    {
                        ArrayPool<byte>.Shared.Return(coalesceBuf);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            //штатная остановка цикла отправки uplink
        }
        catch (Exception ex) when (ex is IOException or SocketException or HttpRequestException or ObjectDisposedException
                                       or InvalidOperationException or OperationCanceledException)
        {
            AppLogger.LogWarning($"[OBXODKA-XHTTP] Uplink writer stopped: {ex.Message}");
            NotifyDropOnce();
        }
        finally
        {
            if (inflightTasks.Count > 0)
            {
                try
                {
                    await Task.WhenAll(inflightTasks).ConfigureAwait(false);
                }
                catch
                {
                    //ошибки фоновых пакетов уже обработаны в DispatchPostBatchAsync
                }
            }
        }
    }

    //асинхронная отправка сформированного батча в параллельном слоте http2 с гарантированным возвратом буфера в пул
    private async Task DispatchPostBatchAsync(
        HttpClient upClient,
        Uri baseEndpoint,
        string hostHeader,
        TlsBrowserProfile profile,
        string hsHeader,
        long timestampSec,
        byte[] rentedBuffer,
        int bodyLength,
        SemaphoreSlim slotGate,
        CancellationToken ct)
    {
        try
        {
            await PostBatchAsync(
                    upClient,
                    baseEndpoint,
                    hostHeader,
                    profile,
                    hsHeader,
                    timestampSec,
                    rentedBuffer.AsMemory(0, bodyLength),
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            //штатная остановка цикла отправки uplink
        }
        catch (Exception ex) when (ex is IOException or SocketException or HttpRequestException or ObjectDisposedException
                                       or InvalidOperationException or OperationCanceledException)
        {
            AppLogger.LogWarning($"[OBXODKA-XHTTP] Parallel uplink POST failed: {ex.Message}");
            NotifyDropOnce();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
            try
            {
                _ = slotGate.Release();
            }
            catch (ObjectDisposedException)
            {
                //семафор слотов уже освобожден при закрытии транспорта
            }
        }
    }

    private static async Task PostBatchAsync(
        HttpClient upClient,
        Uri baseEndpoint,
        string hostHeader,
        TlsBrowserProfile profile,
        string hsHeader,
        long timestampSec,
        ReadOnlyMemory<byte> body,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, baseEndpoint);
        req.Version = HttpVersion.Version20;
        req.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        req.Content = new ReadOnlyMemoryContent(body);
        RoutingHello.ApplyBrowserHeaders(req, profile);
        req.Headers.Host = hostHeader;
        _ = req.Headers.TryAddWithoutValidation("Cookie", ObxodkaFraming.PackStealthCookieHeader("up", hsHeader, timestampSec));
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

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
                var jitteredDelayMs = RandomNumberGenerator.GetInt32(4100, 7600);
                await Task.Delay(jitteredDelayMs, ct).ConfigureAwait(false);
                await SendPingProbeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or ObjectDisposedException or InvalidOperationException)
            {
                //ошибка отдельного пинга игнорируется до следующей итерации
            }
        }
    }

    private void NotifyDropOnce()
    {
        if (!Volatile.Read(ref _connected) || Interlocked.Exchange(ref _dropSignaled, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _connected, false);
        OnConnectionDropped?.Invoke();
    }
}
