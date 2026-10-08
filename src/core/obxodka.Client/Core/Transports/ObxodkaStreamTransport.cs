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
        Channel.CreateUnbounded<(byte[] buffer, int length)>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false }),
        Channel.CreateUnbounded<(byte[] buffer, int length)>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false })
    ];
    private readonly PacketDeduplicator _deduplicator = new();
    private TaskCompletionSource<(string ip, string ip6)> _ipTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string? _configuredSni = configuredSni;
    private readonly int _serverPort = serverPort;
    private Action<Socket>? _protectAction;

    private HttpClient? _httpClient0;
    private SocketsHttpHandler? _handler0;
    private HttpClient? _httpClient1;
    private SocketsHttpHandler? _handler1;
    private CancellationTokenSource? _cts;
    private bool _connected;
    private bool _disposed;
    private int _downlinkPacketCount;
    private int _activeRays = 1;

    public void ProtectSockets(Action<Socket> protectAction) => _protectAction = protectAction;

    private (HttpClient client, SocketsHttpHandler handler) CreateHttpClient(string targetHost, string serverIp, int port)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            KeepAlivePingDelay = TimeSpan.FromSeconds(15),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            EnableMultipleHttp2Connections = true,
            InitialHttp2StreamWindowSize = 4194304,
            SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
                ApplicationProtocols = [SslApplicationProtocol.Http2],
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

                var connectTarget = IPAddress.TryParse(serverIp, out var ipAddr)
                    ? (EndPoint)new IPEndPoint(ipAddr, port)
                    : context.DnsEndPoint;

                await socket.ConnectAsync(connectTarget, cToken).ConfigureAwait(false);
                var netStream = new NetworkStream(socket, ownsSocket: true);
                return IPAddress.TryParse(serverIp, out var parsedIp) && !IPAddress.IsLoopback(parsedIp)
                    ? new DpiBypassStream(netStream)
                    : netStream;
            }
        };

        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        return (client, handler);
    }

    public async Task<(string ip, string ip6)> ConnectAsync(string serverIp, string thumbprint, CancellationToken ct)
    {
        var defaultSni = "obxodka.one";
        try
        {
            if (Uri.TryCreate(AppConfig.ApiBaseUrl, UriKind.Absolute, out var apiUri) &&
                !string.IsNullOrWhiteSpace(apiUri.Host) &&
                !IPAddress.TryParse(apiUri.Host, out _))
            {
                defaultSni = apiUri.Host;
            }
        }
        catch { }

        var sniCandidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(_configuredSni) && !IPAddress.TryParse(_configuredSni, out _))
        {
            sniCandidates.Add(_configuredSni);
        }
        else if (!IPAddress.TryParse(serverIp, out _))
        {
            sniCandidates.Add(serverIp);
        }
        else
        {
            sniCandidates.Add(defaultSni);
        }

        foreach (var candidate in AppSecrets.AllowedSniPool)
        {
            if (!sniCandidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                sniCandidates.Add(candidate);
            }
        }

        int[] portCandidates = _serverPort == 443
            ? [443, 8443, 2053]
            : [_serverPort, 443, 8443];

        var endpointCandidates = new List<(string host, int port)>();
        foreach (var port in portCandidates)
        {
            foreach (var sni in sniCandidates)
            {
                endpointCandidates.Add((sni, port));
            }
        }

        Exception? lastException = null;

        for (var i = 0; i < endpointCandidates.Count; i++)
        {
            var (targetHost, port) = endpointCandidates[i];
            var channelHost = targetHost;

            _cts = new CancellationTokenSource();
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            var attemptTimeoutSeconds = (i == 0 && endpointCandidates.Count > 1) ? 5 : 10;
            attemptCts.CancelAfter(TimeSpan.FromSeconds(attemptTimeoutSeconds));

            _ipTcs = new TaskCompletionSource<(string ip, string ip6)>(TaskCreationOptions.RunContinuationsAsynchronously);

            var ray1Port = port == 443 ? 8443 : 443;
            (_httpClient0, _handler0) = CreateHttpClient(targetHost, serverIp, port);
            (_httpClient1, _handler1) = CreateHttpClient(targetHost, serverIp, ray1Port);

            _ = RunStreamAsync(0, _httpClient0, channelHost, port, thumbprint, attemptCts.Token);

            using (attemptCts.Token.Register(() => _ipTcs.TrySetCanceled()))
            {
                try
                {
                    var result = await _ipTcs.Task.ConfigureAwait(false);
                    _activeRays = 1;
                    EffectivePort = port;
                    _ = Task.Run(() => RunSecondaryRayAsync(channelHost, ray1Port, thumbprint, _cts.Token), _cts.Token);
                    _ = Task.Run(() => DecoyTrafficLoopAsync(_httpClient0, channelHost, port, _cts.Token), _cts.Token);
                    return result;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && i + 1 < endpointCandidates.Count)
                {
                    lastException = ex;
                    Shared.Logging.AppLogger.LogWarning($"[OBXODKA-STREAM] Handshake failed on '{targetHost}:{port}' ({ex.Message}). Hopping to next endpoint in pool...");
                    _cts.Cancel();
                    _httpClient0?.Dispose();
                    _handler0?.Dispose();
                    _httpClient1?.Dispose();
                    _handler1?.Dispose();
                    await Task.Delay(80, ct).ConfigureAwait(false);
                }
            }
        }

        throw lastException ?? new TimeoutException("Failed to connect through available SNI and port pool.");
    }

    private async Task RunSecondaryRayAsync(string channelHost, int initialPort, string thumbprint, CancellationToken ct)
    {
        int[] secondaryPorts = initialPort == 8443 ? [8443, 2053, 443] : [initialPort, 8443, 2053];
        var portIdx = 0;

        while (!ct.IsCancellationRequested && Volatile.Read(ref _connected))
        {
            var activePort = secondaryPorts[portIdx % secondaryPorts.Length];
            try
            {
                await RunStreamAsync(1, _httpClient1!, channelHost, activePort, thumbprint, ct).ConfigureAwait(false);
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
        }
    }

    private async Task DecoyTrafficLoopAsync(HttpClient client, string channelHost, int port, CancellationToken ct)
    {
        string[] decoyPaths = ["/", "/api/v1/health", "/favicon.ico", "/robots.txt"];
        while (!ct.IsCancellationRequested && Volatile.Read(ref _connected))
        {
            try
            {
                var delaySeconds = Random.Shared.Next(25, 55);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);

                if (!Volatile.Read(ref _connected))
                {
                    break;
                }

                var path = decoyPaths[Random.Shared.Next(decoyPaths.Length)];
                var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var decoyUri = $"https://{channelHost}:{port}{path}?_t={nowSec}";
                using var req = new HttpRequestMessage(HttpMethod.Get, decoyUri)
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
                req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
                _ = req.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Not(A:Brand\";v=\"99\", \"Google Chrome\";v=\"133\", \"Chromium\";v=\"133\"");
                _ = req.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
                _ = req.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
                _ = req.Headers.TryAddWithoutValidation("sec-fetch-dest", path == "/" ? "document" : "empty");
                _ = req.Headers.TryAddWithoutValidation("sec-fetch-mode", path == "/" ? "navigate" : "cors");
                _ = req.Headers.TryAddWithoutValidation("sec-fetch-site", "same-origin");

                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    var buf = ArrayPool<byte>.Shared.Rent(512);
                    try
                    {
                        _ = await stream.ReadAsync(buf.AsMemory(0, 512), ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buf);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }
        }
    }

    private async Task RunStreamAsync(int ray, HttpClient client, string channelHost, int port, string thumbprint, CancellationToken ct)
    {
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

            var duplexContent = new PushStreamContent(async (stream, token) =>
            {
                var hsPayload = Encoding.UTF8.GetBytes(thumbprint);
                var hsFrame = ObxodkaFraming.Pack(ObxodkaFraming.CmdHandshake, hsPayload, out var hsTotalLen);
                try
                {
                    await stream.WriteAsync(hsFrame.AsMemory(0, hsTotalLen), token).ConfigureAwait(false);
                    await stream.FlushAsync(token).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(hsFrame);
                }

                var reader = _txChannels[ray].Reader;
                while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
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
                            var isPing = (length == 10 && packet[0] == 0xAA && packet[1] == 0xBB) || length == 8;
                            var cmd = isMtu ? ObxodkaFraming.CmdMtuSync : (isPing ? ObxodkaFraming.CmdPing : ObxodkaFraming.CmdData);
                            var payloadSpan = isMtu ? packet.AsSpan(2, 2) : ((length == 10 && packet[0] == 0xAA && packet[1] == 0xBB) ? packet.AsSpan(2, 8) : packet.AsSpan(0, length));
                            var frame = ObxodkaFraming.Pack(cmd, payloadSpan, out var totalLen, maxMtu: NetworkDefaults.CurrentMtu);
                            try
                            {
                                await stream.WriteAsync(frame.AsMemory(0, totalLen), token).ConfigureAwait(false);
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

                    await stream.FlushAsync(token).ConfigureAwait(false);
                }
            });

            request.Content = duplexContent;

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            _ = response.EnsureSuccessStatusCode();

            using var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var header = new byte[ObxodkaFraming.HeaderSize];

            while (!ct.IsCancellationRequested)
            {
                await responseStream.ReadExactlyAsync(header.AsMemory(0, ObxodkaFraming.HeaderSize), ct).ConfigureAwait(false);

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
                        await responseStream.ReadExactlyAsync(frameBuf.AsMemory(0, remLen), ct).ConfigureAwait(false);
                    }

                    var payloadSpan = frameBuf.AsSpan(0, payloadLen);
                    ObxodkaFraming.UnmaskPayload(payloadSpan, salt);

                    if (command == ObxodkaFraming.CmdHandshakeResponse)
                    {
                        if (ray == 0)
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
                                _ = _ipTcs.TrySetResult((assignedIp, assignedIpV6));
                                Shared.Logging.AppLogger.Log($"[OBXODKA-ESTABLISHED] Tunnel online. IPv4={assignedIp}, IPv6={assignedIpV6}, MTU={NetworkDefaults.CurrentMtu}");
                                _ = Task.Run(() => PingLoopAsync(_cts!.Token), _cts!.Token);
                            }
                        }
                        else
                        {
                            Volatile.Write(ref _activeRays, 2);
                            Shared.Logging.AppLogger.Log("[OBXODKA-STREAM] Ray #1 online and synchronized.");
                        }
                    }
                    else if (command == ObxodkaFraming.CmdData)
                    {
                        if (!_deduplicator.IsDuplicate(payloadSpan))
                        {
                            var count = Interlocked.Increment(ref _downlinkPacketCount);
                            if (count <= 3 || count % 100 == 0)
                            {
                                var proto = payloadLen >= 20 ? ((payloadSpan[0] >> 4) == 4 ? $"IPv4(proto={payloadSpan[9]})" : "IPv6") : "NonIP";
                                Shared.Logging.AppLogger.Log($"[OBXODKA-RX] Downlink packet #{count} (Ray #{ray}): len={payloadLen}B, type={proto}");
                            }
                            var rented = ArrayPool<byte>.Shared.Rent(payloadLen);
                            payloadSpan.CopyTo(rented);
                            OnPacketReceived?.Invoke(rented, payloadLen);
                        }
                    }
                    else if (command == ObxodkaFraming.CmdPong)
                    {
                        if (payloadLen >= 8)
                        {
                            var sendTs = BinaryPrimitives.ReadInt64LittleEndian(payloadSpan[..8]);
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
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Shared.Logging.AppLogger.LogError($"[OBXODKA-STREAM ERROR] Ray #{ray} stream terminated: {ex.Message}");
            if (ray == 0)
            {
                _ = _ipTcs.TrySetException(ex);
            }
            throw;
        }
        finally
        {
            if (ray == 0)
            {
                Volatile.Write(ref _connected, false);
                OnConnectionDropped?.Invoke();
            }
            else
            {
                Volatile.Write(ref _activeRays, 1);
                while (_txChannels[1].Reader.TryRead(out var leftover))
                {
                    if (!_txChannels[0].Writer.TryWrite(leftover))
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
            try
            {
                await Task.Delay(Random.Shared.Next(420, 860), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var nowTs = Stopwatch.GetTimestamp();

            var pingBuf0 = ArrayPool<byte>.Shared.Rent(10);
            pingBuf0[0] = 0xAA;
            pingBuf0[1] = 0xBB;
            BinaryPrimitives.WriteInt64LittleEndian(pingBuf0.AsSpan(2, 8), nowTs);
            if (!_txChannels[0].Writer.TryWrite((pingBuf0, 10)))
            {
                ArrayPool<byte>.Shared.Return(pingBuf0);
            }

            if (_activeRays > 1)
            {
                var pingBuf1 = ArrayPool<byte>.Shared.Rent(10);
                pingBuf1[0] = 0xAA;
                pingBuf1[1] = 0xBB;
                BinaryPrimitives.WriteInt64LittleEndian(pingBuf1.AsSpan(2, 8), nowTs);
                if (!_txChannels[1].Writer.TryWrite((pingBuf1, 10)))
                {
                    ArrayPool<byte>.Shared.Return(pingBuf1);
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

        var activeRays = Volatile.Read(ref _activeRays);
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
        var raysCount = Math.Min(2, Volatile.Read(ref _activeRays));
        for (var r = 0; r < raysCount; r++)
        {
            var pingBuf = ArrayPool<byte>.Shared.Rent(10);
            pingBuf[0] = 0xAA;
            pingBuf[1] = 0xBB;
            BinaryPrimitives.WriteInt64LittleEndian(pingBuf.AsSpan(2, 8), nowTs);
            if (!_txChannels[r].Writer.TryWrite((pingBuf, 10)))
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
        if (!_txChannels[0].Writer.TryWrite((buf, 4)))
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
