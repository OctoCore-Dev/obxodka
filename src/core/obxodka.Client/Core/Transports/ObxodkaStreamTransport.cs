namespace obxodka.Core.Transports;

public sealed class ObxodkaStreamTransport(int serverPort = 443, string? configuredSni = null) : IVpnTransport
{
    public string ProtocolName => "OBXODKA-STREAM";
    public bool IsConnected => Volatile.Read(ref _connected);
    public int EffectivePort { get; private set; } = serverPort;

    public event Action<byte[], int>? OnPacketReceived;
    public event Action<long>? OnPingUpdated;
    public event Action? OnConnectionDropped;
    public static Action<Socket>? OnSocketCreated { get; set; }

    private readonly Channel<(byte[] buffer, int length)>[] _txChannels =
    [
        Channel.CreateBounded<(byte[] buffer, int length)>(new BoundedChannelOptions(2048) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait }),
        Channel.CreateBounded<(byte[] buffer, int length)>(new BoundedChannelOptions(2048) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait })
    ];
    private readonly PacketDeduplicator _deduplicator = new();
    private TaskCompletionSource<(string ip, string ip6)> _ipTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string? _configuredSni = configuredSni;
    private readonly int _serverPort = serverPort;
    private Action<Socket>? _protectAction;
    public string ActiveSni { get; private set; } = AppSecrets.AllowedSniPool[0];

    private HttpClient? _httpClient0;
    private SocketsHttpHandler? _handler0;
    private HttpClient? _httpClient1;
    private SocketsHttpHandler? _handler1;
    private CancellationTokenSource? _cts;
    private bool _connected;
    private bool _disposed;
    private int _activeRays = 1;
    private int _pingLoopStarted;
    private int _preferredRay;
    private long _lastRxTicks0;
    private long _lastRxTicks1;
    private long _lastTxTicks;
    private long _connectedAtTicks;
    private string _activeServerIp = string.Empty;
    private readonly CancellationTokenSource?[] _rayCts = new CancellationTokenSource?[2];

    public void ProtectSockets(Action<Socket> protectAction) => _protectAction = protectAction;

    private (HttpClient client, SocketsHttpHandler handler) CreateHttpClient(string targetHost, string serverIp, int port, bool enableDpiBypass = true)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            Proxy = null,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            KeepAlivePingDelay = Timeout.InfiniteTimeSpan,
            KeepAlivePingTimeout = Timeout.InfiniteTimeSpan,
            EnableMultipleHttp2Connections = true,
            InitialHttp2StreamWindowSize = 4194304,
            SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
                ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
                EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
                RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
                    CertificateValidator.ValidateServerCertificate(certificate, chain, errors)
            },
            ConnectCallback = async (context, cToken) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                OnSocketCreated?.Invoke(socket);
                _protectAction?.Invoke(socket);

                var effectivePort = port;
                var connectTarget = IPAddress.TryParse(serverIp, out var ipAddr)
                    ? (EndPoint)new IPEndPoint(ipAddr, effectivePort)
                    : (context.DnsEndPoint ?? new DnsEndPoint(targetHost, effectivePort));

                Shared.Logging.AppLogger.Log($"[TCP] Dialing socket to {connectTarget} (ServerIp={serverIp}, Port={effectivePort})...");
                var sw = Stopwatch.StartNew();
                try
                {
                    await socket.ConnectAsync(connectTarget, cToken).ConfigureAwait(false);
                    sw.Stop();
                    Shared.Logging.AppLogger.Log($"[TCP OK] Connected to {connectTarget} in {sw.ElapsedMilliseconds}ms.");
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    Shared.Logging.AppLogger.LogError($"[TCP FAIL] Socket failed to connect to {connectTarget} after {sw.ElapsedMilliseconds}ms: {ex.Message}");
                    socket.Dispose();
                    throw;
                }

                var netStream = new NetworkStream(socket, ownsSocket: true);
                return (enableDpiBypass && IPAddress.TryParse(serverIp, out var parsedIp) && !IPAddress.IsLoopback(parsedIp))
                    ? new DpiBypassStream(netStream, bypassEnabled: true)
                    : netStream;
            }
        };

        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        return (client, handler);
    }

    private HttpClient ResetRayHttpClient(int ray, string targetHost, int port, bool enableDpiBypass = true)
    {
        if (ray == 0)
        {
            _httpClient0?.Dispose();
            _handler0?.Dispose();
            (_httpClient0, _handler0) = CreateHttpClient(targetHost, _activeServerIp, port, enableDpiBypass);
            return _httpClient0;
        }

        _httpClient1?.Dispose();
        _handler1?.Dispose();
        (_httpClient1, _handler1) = CreateHttpClient(targetHost, _activeServerIp, port, enableDpiBypass);
        return _httpClient1;
    }

    public async Task<(string ip, string ip6)> ConnectAsync(string serverIp, string thumbprint, CancellationToken ct)
    {
        _activeServerIp = serverIp;
        var defaultSni = AppSecrets.AllowedSniPool[0];

        var sniCandidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(_configuredSni) &&
            !IPAddress.TryParse(_configuredSni, out _) &&
            !_configuredSni.Contains("obxodka", StringComparison.OrdinalIgnoreCase) &&
            !_configuredSni.Contains("octocore", StringComparison.OrdinalIgnoreCase))
        {
            sniCandidates.Add(_configuredSni);
        }
        else if (!IPAddress.TryParse(serverIp, out _) &&
                 !serverIp.Contains("obxodka", StringComparison.OrdinalIgnoreCase) &&
                 !serverIp.Contains("octocore", StringComparison.OrdinalIgnoreCase))
        {
            sniCandidates.Add(serverIp);
        }
        else
        {
            sniCandidates.Add(defaultSni);
        }

        foreach (var candidate in AppSecrets.GetCandidateSniList(8))
        {
            if (!sniCandidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                sniCandidates.Add(candidate);
            }
        }

        int[] portCandidates = _serverPort == 443
            ? [443, 8443]
            : [_serverPort, 443, 8443];

        var endpointCandidates = new List<(string host, int port, bool enableDpi)>();
        foreach (var port in portCandidates)
        {
            for (var sIdx = 0; sIdx < sniCandidates.Count; sIdx++)
            {
                var sni = sniCandidates[sIdx];
                endpointCandidates.Add((sni, port, true));
                if (sIdx < 2 && port == 443)
                {
                    endpointCandidates.Add((sni, port, false));
                }
            }
        }

        Exception? lastException = null;

        for (var i = 0; i < endpointCandidates.Count; i++)
        {
            var (targetHost, port, enableDpi) = endpointCandidates[i];
            var channelHost = targetHost;

            var attemptTimeoutSeconds = (i == 0 && endpointCandidates.Count > 1) ? 3 : 4;
            var dpiModeStr = enableDpi ? "Clean-Split DPI" : "Direct Stream";
            Shared.Logging.AppLogger.Log($"[OBXODKA-STREAM] Candidate #{i + 1}/{endpointCandidates.Count}: Attempting node {serverIp}:{port} with SNI '{targetHost}' [{dpiModeStr}] (Timeout: {attemptTimeoutSeconds}s)...");

            _cts = new CancellationTokenSource();
            _ipTcs = new TaskCompletionSource<(string ip, string ip6)>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _lastRxTicks0, 0);
            Volatile.Write(ref _lastRxTicks1, 0);

            var ray1Port = port == 443 ? 8443 : 443;
            (_httpClient0, _handler0) = CreateHttpClient(targetHost, serverIp, port, enableDpi);
            (_httpClient1, _handler1) = CreateHttpClient(targetHost, serverIp, ray1Port, enableDpi);

            using var attemptTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(attemptTimeoutSeconds));
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token, attemptTimeoutCts.Token);

            _ = Task.Run(() => RunPrimaryRayLoopAsync(channelHost, port, thumbprint, enableDpi, _cts.Token), _cts.Token);
            _ = Task.Run(() => RunSecondaryRayAsync(channelHost, ray1Port, thumbprint, enableDpi, _cts.Token), _cts.Token);

            using (handshakeCts.Token.Register(() => _ipTcs.TrySetCanceled()))
            {
                try
                {
                    var result = await _ipTcs.Task.ConfigureAwait(false);
                    ActiveSni = channelHost;
                    Shared.Logging.AppLogger.Log($"[OBXODKA-STREAM] Successfully connected to node physical IP {serverIp}:{EffectivePort} via {channelHost}. Established tunnel assigned IPv4: {result.ip}, IPv6: {result.ip6}");
                    return result;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && i + 1 < endpointCandidates.Count)
                {
                    lastException = ex;
                    var inner = ex.InnerException != null ? $" -> {ex.InnerException.GetType().Name}: {ex.InnerException.Message}" : "";
                    Shared.Logging.AppLogger.LogWarning($"[OBXODKA-STREAM] Handshake attempt #{i + 1} failed on '{targetHost}:{port}' [{dpiModeStr}] ({ex.GetType().Name}: {ex.Message}{inner}). Hopping to next endpoint in pool...");
                    _cts.Cancel();
                    _httpClient0?.Dispose();
                    _handler0?.Dispose();
                    _httpClient1?.Dispose();
                    _handler1?.Dispose();
                    await Task.Delay(50, ct).ConfigureAwait(false);
                }
            }
        }

        throw lastException ?? new TimeoutException("Failed to connect through available SNI and port pool.");
    }

    private async Task RunPrimaryRayLoopAsync(string channelHost, int initialPort, string thumbprint, bool enableDpi, CancellationToken ct)
    {
        int[] primaryPorts = initialPort == 443 ? [443, 8443] : [initialPort, 443, 8443];
        var portIdx = 0;
        var consecutiveDrops = 0;

        while (!ct.IsCancellationRequested && (Volatile.Read(ref _connected) || !_ipTcs.Task.IsCompleted))
        {
            var activePort = primaryPorts[portIdx % primaryPorts.Length];
            var client = portIdx == 0 && _httpClient0 != null ? _httpClient0 : ResetRayHttpClient(0, channelHost, activePort, enableDpi);
            var rxBefore = Volatile.Read(ref _lastRxTicks0);
            try
            {
                await RunStreamAsync(0, client, channelHost, activePort, thumbprint, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Shared.Logging.AppLogger.LogWarning($"[OBXODKA-STREAM] Ray #0 socket aborted on port {activePort} by health watchdog. Failing over to next port...");
                portIdx++;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (Volatile.Read(ref _connected))
                {
                    Shared.Logging.AppLogger.LogWarning($"[OBXODKA-STREAM] Ray #0 dropped on port {activePort}: {ex.Message}. Reconnecting in 1s...");
                }
                portIdx++;
            }

            if (Volatile.Read(ref _lastRxTicks0) > rxBefore)
            {
                consecutiveDrops = 0;
            }
            else
            {
                consecutiveDrops++;
            }

            if (consecutiveDrops >= primaryPorts.Length && _rayCts[1] == null && Volatile.Read(ref _connected) && !ct.IsCancellationRequested)
            {
                Shared.Logging.AppLogger.LogWarning("[OBXODKA-STREAM] Both rays dropped across all ports. Signaling connection drop.");
                Volatile.Write(ref _connected, false);
                OnConnectionDropped?.Invoke();
                break;
            }

            if (!ct.IsCancellationRequested && Volatile.Read(ref _connected))
            {
                try
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
            }
            else if (!_ipTcs.Task.IsCompleted)
            {
                break;
            }
        }
    }

    private async Task RunSecondaryRayAsync(string channelHost, int initialPort, string thumbprint, bool enableDpi, CancellationToken ct)
    {
        int[] secondaryPorts = initialPort == 8443 ? [8443, 443] : [initialPort, 8443, 443];
        var portIdx = 0;

        while (!ct.IsCancellationRequested && (Volatile.Read(ref _connected) || !_ipTcs.Task.IsCompleted))
        {
            var activePort = secondaryPorts[portIdx % secondaryPorts.Length];
            var client = portIdx == 0 && _httpClient1 != null ? _httpClient1 : ResetRayHttpClient(1, channelHost, activePort, enableDpi);
            try
            {
                await RunStreamAsync(1, client, channelHost, activePort, thumbprint, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Shared.Logging.AppLogger.LogWarning($"[OBXODKA-STREAM] Ray #1 socket aborted on port {activePort} by health watchdog. Failing over to next port...");
                portIdx++;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Shared.Logging.AppLogger.LogWarning($"[OBXODKA-STREAM] Ray #1 dropped on port {activePort}: {ex.Message}. Reconnecting in 2s...");
                portIdx++;
            }

            if (!ct.IsCancellationRequested && Volatile.Read(ref _connected))
            {
                try
                {
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
            }
            else if (!_ipTcs.Task.IsCompleted)
            {
                break;
            }
        }
    }

    private async Task RunStreamAsync(int ray, HttpClient client, string channelHost, int port, string thumbprint, CancellationToken ct)
    {
        using var rayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _rayCts[ray] = rayCts;
        var token = rayCts.Token;

        try
        {
            var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var requestUri = $"https://{channelHost}:{port}/api/v1/sync?ray={ray}&mtu={NetworkDefaults.CurrentMtu}&_t={nowSec}&v=133.0";
            var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };

            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
            request.Headers.Accept.ParseAdd("*/*");
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            _ = request.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Not(A:Brand\";v=\"99\", \"Google Chrome\";v=\"133\", \"Chromium\";v=\"133\"");
            _ = request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
            _ = request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
            _ = request.Headers.TryAddWithoutValidation("sec-fetch-dest", "empty");
            _ = request.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
            _ = request.Headers.TryAddWithoutValidation("sec-fetch-site", "same-origin");

            var duplexContent = new PushStreamContent(async (stream, pushToken) =>
            {
                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(token, pushToken);
                var pumpToken = pumpCts.Token;
                var coalesceBuf = ArrayPool<byte>.Shared.Rent(16384);

                try
                {
                    var hsPayload = Encoding.UTF8.GetBytes(thumbprint);
                    var hsFrame = ObxodkaFraming.Pack(ObxodkaFraming.CmdHandshake, hsPayload, out var hsTotalLen);
                    try
                    {
                        await stream.WriteAsync(hsFrame.AsMemory(0, hsTotalLen), pumpToken).ConfigureAwait(false);
                        await stream.FlushAsync(pumpToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(hsFrame);
                    }

                    var reader = _txChannels[ray].Reader;
                    while (await reader.WaitToReadAsync(pumpToken).ConfigureAwait(false))
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
                                var isPing = (length >= 10 && packet[0] == 0xAA && packet[1] == 0xBB) || length == 8;
                                var cmd = isMtu ? ObxodkaFraming.CmdMtuSync : (isPing ? ObxodkaFraming.CmdPing : ObxodkaFraming.CmdData);
                                var payloadSpan = isMtu
                                    ? packet.AsSpan(2, 2)
                                    : ((length >= 10 && packet[0] == 0xAA && packet[1] == 0xBB)
                                        ? packet.AsSpan(2, length - 2)
                                        : packet.AsSpan(0, length));

                                var frame = ObxodkaFraming.Pack(cmd, payloadSpan, out var totalLen, maxMtu: NetworkDefaults.CurrentMtu);
                                try
                                {
                                    if (coalescedLen + totalLen > coalesceBuf.Length && coalescedLen > 0)
                                    {
                                        await stream.WriteAsync(coalesceBuf.AsMemory(0, coalescedLen), pumpToken).ConfigureAwait(false);
                                        coalescedLen = 0;
                                    }

                                    if (totalLen <= coalesceBuf.Length)
                                    {
                                        Buffer.BlockCopy(frame, 0, coalesceBuf, coalescedLen, totalLen);
                                        coalescedLen += totalLen;
                                    }
                                    else
                                    {
                                        await stream.WriteAsync(frame.AsMemory(0, totalLen), pumpToken).ConfigureAwait(false);
                                    }
                                }
                                finally
                                {
                                    ArrayPool<byte>.Shared.Return(frame);
                                }
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(packet);
                            }
                        }

                        if (coalescedLen > 0)
                        {
                            await stream.WriteAsync(coalesceBuf.AsMemory(0, coalescedLen), pumpToken).ConfigureAwait(false);
                        }

                        await stream.FlushAsync(pumpToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Shared.Logging.AppLogger.LogWarning($"[OBXODKA-TX] Ray #{ray} upload pump stopped: {ex.Message}");
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(coalesceBuf);
                }
            });

            request.Content = duplexContent;

            Shared.Logging.AppLogger.Log($"[H2 STREAM] Ray #{ray} initiating duplex tunnel POST to https://{channelHost}:{port}/api/v1/sync (MTU={NetworkDefaults.CurrentMtu})...");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            Shared.Logging.AppLogger.Log($"[H2 RESPONSE] Ray #{ray} received HTTP {(int)response.StatusCode} {response.ReasonPhrase} (Version={response.Version}).");

            if (!response.IsSuccessStatusCode)
            {
                var rawErr = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                Shared.Logging.AppLogger.LogError($"[H2 ERROR] Ray #{ray} server rejected stream: HTTP {(int)response.StatusCode} - {rawErr}");
                _ = response.EnsureSuccessStatusCode();
            }

            using var responseStream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var header = new byte[ObxodkaFraming.HeaderSize];

            while (!token.IsCancellationRequested)
            {
                await responseStream.ReadExactlyAsync(header.AsMemory(0, ObxodkaFraming.HeaderSize), token).ConfigureAwait(false);

                if (!ObxodkaFraming.TryDecodeHeader(header, out var totalLen, out var payloadLen, out var command, out var salt))
                {
                    Shared.Logging.AppLogger.LogError($"[OBXODKA-STREAM] Ray #{ray} desync: invalid frame header.");
                    break;
                }

                var remLen = totalLen - ObxodkaFraming.HeaderSize;
                var frameBuf = ArrayPool<byte>.Shared.Rent(remLen);
                try
                {
                    if (remLen > 0)
                    {
                        await responseStream.ReadExactlyAsync(frameBuf.AsMemory(0, remLen), token).ConfigureAwait(false);
                    }

                    var payloadSpan = frameBuf.AsSpan(0, payloadLen);
                    ObxodkaFraming.UnmaskPayload(payloadSpan, salt);

                    if (ray == 0)
                    {
                        Volatile.Write(ref _lastRxTicks0, Stopwatch.GetTimestamp());
                    }
                    else if (ray == 1)
                    {
                        Volatile.Write(ref _lastRxTicks1, Stopwatch.GetTimestamp());
                    }

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
                            Volatile.Write(ref _connected, true);
                            var isFirst = _ipTcs.TrySetResult((assignedIp, assignedIpV6));

                            if (isFirst)
                            {
                                Volatile.Write(ref _connectedAtTicks, Stopwatch.GetTimestamp());
                                Volatile.Write(ref _preferredRay, ray);
                                EffectivePort = port;
                                Shared.Logging.AppLogger.Log($"[OBXODKA-ESTABLISHED] Tunnel online via Ray #{ray} (Port {port}). IPv4={assignedIp}, IPv6={assignedIpV6}, MTU={NetworkDefaults.CurrentMtu}");
                                if (Interlocked.Exchange(ref _pingLoopStarted, 1) == 0)
                                {
                                    _ = Task.Run(() => PingLoopAsync(_cts!.Token), _cts!.Token);
                                }
                            }
                            else
                            {
                                Volatile.Write(ref _activeRays, 2);
                                Shared.Logging.AppLogger.Log($"[OBXODKA-STREAM] Ray #{ray} online and synchronized on port {port}. Active rays = 2.");
                            }
                        }
                    }
                    else if (command == ObxodkaFraming.CmdData)
                    {
                        if (payloadLen >= 20 && !_deduplicator.IsDuplicate(payloadSpan))
                        {
                            var rented = ArrayPool<byte>.Shared.Rent(payloadLen);
                            payloadSpan.CopyTo(rented);
                            OnPacketReceived?.Invoke(rented, payloadLen);
                        }
                    }
                    else if (command == ObxodkaFraming.CmdPong)
                    {
                        var tsSpan = (payloadLen >= 10 && payloadSpan[0] == 0xAA && payloadSpan[1] == 0xBB)
                            ? payloadSpan.Slice(2, 8)
                            : (payloadLen >= 8 ? payloadSpan[..8] : default);

                        if (!tsSpan.IsEmpty)
                        {
                            var sendTs = BinaryPrimitives.ReadInt64LittleEndian(tsSpan);
                            var now = Stopwatch.GetTimestamp();
                            var elapsedMs = (long)((now - sendTs) * 1000.0 / Stopwatch.Frequency);
                            if (elapsedMs is >= 0 and < 5000)
                            {
                                OnPingUpdated?.Invoke(elapsedMs);
                            }
                        }
                    }
                    else if (command == ObxodkaFraming.CmdMtuSync)
                    {
                        if (payloadLen >= 2)
                        {
                            var ackMtu = (int)BinaryPrimitives.ReadUInt16BigEndian(payloadSpan[..2]);
                            Shared.Logging.AppLogger.Log($"[OBXODKA-STREAM] Server confirmed locked MTU {ackMtu}B");
                        }
                    }
                    else if (command == ObxodkaFraming.CmdDisconnect)
                    {
                        Shared.Logging.AppLogger.Log($"[OBXODKA-STREAM] Server signaled disconnect on Ray #{ray}.");
                        break;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(frameBuf);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Shared.Logging.AppLogger.LogError($"[OBXODKA-STREAM ERROR] Ray #{ray} stream terminated: {ex.Message}");
            throw;
        }
        finally
        {
            _rayCts[ray] = null;
            var otherRay = ray == 0 ? 1 : 0;
            var isOtherAlive = _rayCts[otherRay] != null;

            if (isOtherAlive && Volatile.Read(ref _connected) && _cts?.IsCancellationRequested != true)
            {
                Volatile.Write(ref _activeRays, 1);
                Volatile.Write(ref _preferredRay, otherRay);
                Shared.Logging.AppLogger.LogWarning($"[OBXODKA-STREAM] Ray #{ray} offline. Migrated preferred ray to #{otherRay}.");
                while (_txChannels[ray].Reader.TryRead(out var leftover))
                {
                    if (!_txChannels[otherRay].Writer.TryWrite(leftover))
                    {
                        ArrayPool<byte>.Shared.Return(leftover.buffer);
                    }
                }
            }
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var nowTs = Stopwatch.GetTimestamp();
            var rx0 = Volatile.Read(ref _lastRxTicks0);
            var rx1 = Volatile.Read(ref _lastRxTicks1);
            var lastActivityTicks = Math.Max(Volatile.Read(ref _lastTxTicks), Math.Max(rx0, rx1));
            var idleSec = lastActivityTicks > 0 ? (nowTs - lastActivityTicks) / (double)Stopwatch.Frequency : 0;
            var delayMs = idleSec < 3.0 ? Random.Shared.Next(450, 900) : Random.Shared.Next(2500, 4500);

            try
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            nowTs = Stopwatch.GetTimestamp();
            rx0 = Volatile.Read(ref _lastRxTicks0);
            rx1 = Volatile.Read(ref _lastRxTicks1);
            var connTicks = Volatile.Read(ref _connectedAtTicks);
            var activeRays = Volatile.Read(ref _activeRays);

            if (connTicks > 0 && ((nowTs - connTicks) * 1000.0 / Stopwatch.Frequency) > 8000)
            {
                if (rx0 == 0 && rx1 > 0 && _rayCts[0] != null)
                {
                    Shared.Logging.AppLogger.LogWarning("[RAY-HEALTH] Ray #0 never completed handshake. Aborting to hop port.");
                    Volatile.Write(ref _lastRxTicks0, nowTs);
                    try
                    { _rayCts[0]?.Cancel(); }
                    catch { }
                }
                else if (rx1 == 0 && rx0 > 0 && _rayCts[1] != null)
                {
                    Shared.Logging.AppLogger.LogWarning("[RAY-HEALTH] Ray #1 never completed handshake. Aborting to hop port.");
                    Volatile.Write(ref _lastRxTicks1, nowTs);
                    try
                    { _rayCts[1]?.Cancel(); }
                    catch { }
                }
            }

            if (rx0 > 0 && rx1 > 0 && activeRays > 1)
            {
                var silence0Ms = (nowTs - rx0) * 1000.0 / Stopwatch.Frequency;
                var silence1Ms = (nowTs - rx1) * 1000.0 / Stopwatch.Frequency;

                if (silence0Ms > 8000 && silence1Ms < 3000)
                {
                    Shared.Logging.AppLogger.LogWarning($"[RAY-HEALTH] Ray #0 stalled (silence {silence0Ms:F0}ms). Aborting Ray #0 socket & shifting 100% traffic to Ray #1.");
                    Volatile.Write(ref _activeRays, 1);
                    Volatile.Write(ref _preferredRay, 1);
                    try
                    { _rayCts[0]?.Cancel(); }
                    catch { }
                    while (_txChannels[0].Reader.TryRead(out var leftover))
                    {
                        if (!_txChannels[1].Writer.TryWrite(leftover))
                        {
                            ArrayPool<byte>.Shared.Return(leftover.buffer);
                        }
                    }
                }
                else if (silence1Ms > 8000 && silence0Ms < 3000)
                {
                    Shared.Logging.AppLogger.LogWarning($"[RAY-HEALTH] Ray #1 stalled (silence {silence1Ms:F0}ms). Aborting Ray #1 socket & shifting 100% traffic to Ray #0.");
                    Volatile.Write(ref _activeRays, 1);
                    Volatile.Write(ref _preferredRay, 0);
                    try
                    { _rayCts[1]?.Cancel(); }
                    catch { }
                    while (_txChannels[1].Reader.TryRead(out var leftover))
                    {
                        if (!_txChannels[0].Writer.TryWrite(leftover))
                        {
                            ArrayPool<byte>.Shared.Return(leftover.buffer);
                        }
                    }
                }
                else if (silence0Ms < 3000 && silence1Ms < 3000 && activeRays < 2)
                {
                    Volatile.Write(ref _activeRays, 2);
                }
            }
            else if (activeRays <= 1)
            {
                var pref = Volatile.Read(ref _preferredRay);
                var rxPref = pref == 0 ? rx0 : rx1;
                if (rxPref > 0)
                {
                    var silenceMs = (nowTs - rxPref) * 1000.0 / Stopwatch.Frequency;
                    if (silenceMs > 25000)
                    {
                        Shared.Logging.AppLogger.LogWarning($"[RAY-HEALTH] Active Ray #{pref} stalled (silence {silenceMs:F0}ms). Aborting socket to trigger port failover.");
                        Volatile.Write(ref pref == 0 ? ref _lastRxTicks0 : ref _lastRxTicks1, nowTs);
                        try
                        { _rayCts[pref]?.Cancel(); }
                        catch { }
                    }
                }
            }

            byte rxAliveMask = 0;
            if (rx0 > 0 && ((nowTs - rx0) * 1000.0 / Stopwatch.Frequency) < 10000)
            {
                rxAliveMask |= 0x01;
            }
            if (rx1 > 0 && ((nowTs - rx1) * 1000.0 / Stopwatch.Frequency) < 10000)
            {
                rxAliveMask |= 0x02;
            }
            if (rxAliveMask == 0)
            {
                rxAliveMask = (byte)(1 << Volatile.Read(ref _preferredRay));
            }

            for (var r = 0; r < 2; r++)
            {
                var pingBuf = ArrayPool<byte>.Shared.Rent(11);
                pingBuf[0] = 0xAA;
                pingBuf[1] = 0xBB;
                BinaryPrimitives.WriteInt64LittleEndian(pingBuf.AsSpan(2, 8), nowTs);
                pingBuf[10] = rxAliveMask;
                if (!_txChannels[r].Writer.TryWrite((pingBuf, 11)))
                {
                    ArrayPool<byte>.Shared.Return(pingBuf);
                }
            }
        }
    }

    public void SendPacketFromPool(byte[] packet, int length)
    {
        if (!Volatile.Read(ref _connected))
        {
            ArrayPool<byte>.Shared.Return(packet);
            return;
        }

        Volatile.Write(ref _lastTxTicks, Stopwatch.GetTimestamp());

        var activeRays = Volatile.Read(ref _activeRays);
        if (activeRays <= 1)
        {
            var targetRay = Volatile.Read(ref _preferredRay);
            if (!_txChannels[targetRay].Writer.TryWrite((packet, length)))
            {
                ArrayPool<byte>.Shared.Return(packet);
            }
            return;
        }

        PacketRouter.GetRays(packet.AsSpan(0, length), activeRays, out var primaryRay, out var secondaryRay);

        var ray0 = primaryRay % activeRays;
        if (!_txChannels[ray0].Writer.TryWrite((packet, length)))
        {
            ArrayPool<byte>.Shared.Return(packet);
            return;
        }

        if (secondaryRay >= 0 && secondaryRay < activeRays && secondaryRay != ray0)
        {
            var dup = ArrayPool<byte>.Shared.Rent(length);
            Buffer.BlockCopy(packet, 0, dup, 0, length);
            if (!_txChannels[secondaryRay].Writer.TryWrite((dup, length)))
            {
                ArrayPool<byte>.Shared.Return(dup);
            }
        }
    }

    public Task SendPingProbeAsync()
    {
        var nowTs = Stopwatch.GetTimestamp();
        var rx0 = Volatile.Read(ref _lastRxTicks0);
        var rx1 = Volatile.Read(ref _lastRxTicks1);
        byte rxAliveMask = 0;
        if (rx0 > 0 && ((nowTs - rx0) * 1000.0 / Stopwatch.Frequency) < 3500)
        {
            rxAliveMask |= 0x01;
        }
        if (rx1 > 0 && ((nowTs - rx1) * 1000.0 / Stopwatch.Frequency) < 3500)
        {
            rxAliveMask |= 0x02;
        }
        if (rxAliveMask == 0)
        {
            rxAliveMask = (byte)(1 << Volatile.Read(ref _preferredRay));
        }

        for (var r = 0; r < 2; r++)
        {
            var pingBuf = ArrayPool<byte>.Shared.Rent(11);
            pingBuf[0] = 0xAA;
            pingBuf[1] = 0xBB;
            BinaryPrimitives.WriteInt64LittleEndian(pingBuf.AsSpan(2, 8), nowTs);
            pingBuf[10] = rxAliveMask;
            if (!_txChannels[r].Writer.TryWrite((pingBuf, 11)))
            {
                ArrayPool<byte>.Shared.Return(pingBuf);
            }
        }
        return Task.CompletedTask;
    }

    public Task SendDisconnectSignalAsync()
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    public Task SendMtuSyncAsync(int mtu)
    {
        var buf = ArrayPool<byte>.Shared.Rent(4);
        buf[0] = 0xCC;
        buf[1] = 0xDD;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), (ushort)mtu);
        var targetRay = Volatile.Read(ref _activeRays) > 1 ? 0 : Volatile.Read(ref _preferredRay);
        if (!_txChannels[targetRay].Writer.TryWrite((buf, 4)))
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
        _cts?.Cancel();
        for (var i = 0; i < _txChannels.Length; i++)
        {
            _ = _txChannels[i].Writer.TryComplete();
            while (_txChannels[i].Reader.TryRead(out var item))
            {
                if (item.buffer != null)
                {
                    ArrayPool<byte>.Shared.Return(item.buffer);
                }
            }
        }
        _httpClient0?.Dispose();
        _handler0?.Dispose();
        _httpClient1?.Dispose();
        _handler1?.Dispose();
        _cts?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class PushStreamContent(Func<Stream, CancellationToken, Task> onSerialize) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            onSerialize(stream, cancellationToken);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
