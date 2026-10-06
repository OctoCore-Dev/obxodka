namespace obxodka.Core.Transports;

public sealed class ObxodkaStreamTransport(int serverPort = 443, string? configuredSni = null) : IVpnTransport
{
    public string ProtocolName => "OBXODKA-STREAM";
    public bool IsConnected => Volatile.Read(ref _connected);

    public event Action<byte[], int>? OnPacketReceived;
    public event Action<long>? OnPingUpdated;
    public event Action? OnConnectionDropped;
    public static event Action<Socket>? OnSocketCreated;

    private readonly PriorityPacketQueue _txQueue = new(3000);
    private readonly TaskCompletionSource<(string ip, string ip6)> _ipTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string? _configuredSni = configuredSni;
    private readonly int _serverPort = serverPort;
    private Action<Socket>? _protectAction;

    private HttpClient? _httpClient;
    private SocketsHttpHandler? _handler;
    private CancellationTokenSource? _cts;
    private bool _connected;
    private bool _disposed;
    private int _downlinkPacketCount;

    public void ProtectSockets(Action<Socket> protectAction) => _protectAction = protectAction;

    public async Task<(string ip, string ip6)> ConnectAsync(string serverIp, string thumbprint, CancellationToken ct)
    {
        _cts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);

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

        var targetHost = !string.IsNullOrWhiteSpace(_configuredSni) && !IPAddress.TryParse(_configuredSni, out _)
            ? _configuredSni
            : (!IPAddress.TryParse(serverIp, out _) ? serverIp : defaultSni);

        var channelHost = !string.IsNullOrWhiteSpace(targetHost) ? targetHost : serverIp;

        _handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            KeepAlivePingDelay = TimeSpan.FromSeconds(15),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            EnableMultipleHttp2Connections = true,
            InitialHttp2StreamWindowSize = 2097152,
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
                    ? (EndPoint)new IPEndPoint(ipAddr, _serverPort)
                    : context.DnsEndPoint;

                await socket.ConnectAsync(connectTarget, cToken).ConfigureAwait(false);
                var netStream = new NetworkStream(socket, ownsSocket: true);
                return IPAddress.TryParse(serverIp, out var parsedIp) && !IPAddress.IsLoopback(parsedIp)
                    ? new DpiBypassStream(netStream)
                    : netStream;
            }
        };

        _httpClient = new HttpClient(_handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        _ = RunStreamAsync(channelHost, thumbprint, linkedCts.Token);

        linkedCts.CancelAfter(TimeSpan.FromSeconds(15));
        using (linkedCts.Token.Register(() => _ipTcs.TrySetCanceled()))
        {
            return await _ipTcs.Task.ConfigureAwait(false);
        }
    }

    private async Task RunStreamAsync(string channelHost, string thumbprint, CancellationToken ct)
    {
        try
        {
            var requestUri = $"https://{channelHost}:{_serverPort}/api/v1/sync";
            var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };

            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
            request.Headers.Accept.ParseAdd("*/*");
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

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

                while (!token.IsCancellationRequested)
                {
                    var (packet, length) = await _txQueue.DequeueAsync(token).ConfigureAwait(false);
                    if (packet == null || length <= 0)
                    {
                        continue;
                    }

                    try
                    {
                        var isPing = length == 8 && packet[0] == 0xAA && packet[1] == 0xBB;
                        var cmd = isPing ? ObxodkaFraming.CmdPing : ObxodkaFraming.CmdData;
                        var frame = ObxodkaFraming.Pack(cmd, packet.AsSpan(0, length), out var totalLen, maxMtu: 1360);
                        try
                        {
                            await stream.WriteAsync(frame.AsMemory(0, totalLen), token).ConfigureAwait(false);
                            await stream.FlushAsync(token).ConfigureAwait(false);
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
            });

            request.Content = duplexContent;

            using var response = await _httpClient!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            _ = response.EnsureSuccessStatusCode();

            using var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var header = new byte[ObxodkaFraming.HeaderSize];

            while (!ct.IsCancellationRequested)
            {
                await responseStream.ReadExactlyAsync(header.AsMemory(0, ObxodkaFraming.HeaderSize), ct).ConfigureAwait(false);

                if (!ObxodkaFraming.TryDecodeHeader(header, out var totalLen, out var payloadLen, out var command))
                {
                    Shared.Logging.AppLogger.LogError("[OBXODKA-STREAM] Stream desync: invalid frame header.");
                    break;
                }

                var payload = ArrayPool<byte>.Shared.Rent(payloadLen);
                try
                {
                    if (payloadLen > 0)
                    {
                        await responseStream.ReadExactlyAsync(payload.AsMemory(0, payloadLen), ct).ConfigureAwait(false);
                    }

                    var paddingLen = totalLen - ObxodkaFraming.HeaderSize - payloadLen;
                    if (paddingLen > 0)
                    {
                        var trash = ArrayPool<byte>.Shared.Rent(paddingLen);
                        try
                        {
                            await responseStream.ReadExactlyAsync(trash.AsMemory(0, paddingLen), ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(trash);
                        }
                    }

                    if (command == ObxodkaFraming.CmdHandshakeResponse)
                    {
                        var info = Encoding.UTF8.GetString(payload.AsSpan(0, payloadLen));
                        if (info.StartsWith("IP:", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = info[3..].Split('|');
                            var assignedIp = parts[0];
                            var assignedIpV6 = parts.Length > 1 ? parts[1] : string.Empty;
                            Volatile.Write(ref _connected, true);
                            _ = _ipTcs.TrySetResult((assignedIp, assignedIpV6));
                            Shared.Logging.AppLogger.Log($"[OBXODKA-ESTABLISHED] Tunnel online. IPv4={assignedIp}, IPv6={assignedIpV6}");
                            _ = Task.Run(() => PingLoopAsync(_cts!.Token), _cts!.Token);
                        }
                    }
                    else if (command == ObxodkaFraming.CmdData)
                    {
                        var count = Interlocked.Increment(ref _downlinkPacketCount);
                        if (count <= 3 || count % 100 == 0)
                        {
                            var proto = payloadLen >= 20 ? ((payload[0] >> 4) == 4 ? $"IPv4(proto={payload[9]})" : "IPv6") : "NonIP";
                            Shared.Logging.AppLogger.Log($"[OBXODKA-RX] Downlink packet #{count}: len={payloadLen}B, type={proto}");
                        }
                        OnPacketReceived?.Invoke(payload, payloadLen);
                    }
                    else if (command == ObxodkaFraming.CmdPong)
                    {
                        if (payloadLen >= 8)
                        {
                            var sendTs = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(0, 8));
                            var now = Stopwatch.GetTimestamp();
                            var elapsedMs = (long)((now - sendTs) * 1000.0 / Stopwatch.Frequency);
                            if (elapsedMs is >= 0 and < 5000)
                            {
                                OnPingUpdated?.Invoke(elapsedMs);
                            }
                        }
                    }
                    else if (command == ObxodkaFraming.CmdDisconnect)
                    {
                        Shared.Logging.AppLogger.Log("[OBXODKA-STREAM] Server signaled disconnect.");
                        break;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(payload);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Shared.Logging.AppLogger.LogError($"[OBXODKA-STREAM ERROR] Stream terminated: {ex.Message}");
            _ = _ipTcs.TrySetException(ex);
        }
        finally
        {
            Volatile.Write(ref _connected, false);
            OnConnectionDropped?.Invoke();
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2.5));
        while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var pingBuf = ArrayPool<byte>.Shared.Rent(8);
            BinaryPrimitives.WriteInt64LittleEndian(pingBuf.AsSpan(0, 8), Stopwatch.GetTimestamp());
            if (!_txQueue.TryEnqueue(pingBuf, 8))
            {
                ArrayPool<byte>.Shared.Return(pingBuf);
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

        if (!_txQueue.TryEnqueue(packet, length))
        {
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    public Task SendPingProbeAsync()
    {
        var pingBuf = ArrayPool<byte>.Shared.Rent(8);
        BinaryPrimitives.WriteInt64LittleEndian(pingBuf.AsSpan(0, 8), Stopwatch.GetTimestamp());
        if (!_txQueue.TryEnqueue(pingBuf, 8))
        {
            ArrayPool<byte>.Shared.Return(pingBuf);
        }
        return Task.CompletedTask;
    }

    public Task SendDisconnectSignalAsync()
    {
        _cts?.Cancel();
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
        _txQueue.DrainAndReturn(b => ArrayPool<byte>.Shared.Return(b));
        _txQueue.Dispose();
        _httpClient?.Dispose();
        _handler?.Dispose();
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
