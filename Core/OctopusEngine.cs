namespace obxodka.Core;

internal sealed partial class OctopusEngine : IDisposable, IAsyncDisposable
{
    private static readonly Lazy<OctopusEngine> t_instance = new(
        () => new OctopusEngine(),
        LazyThreadSafetyMode.ExecutionAndPublication
    );
    public static OctopusEngine Current => t_instance.Value;
    private GrpcChannel? _grpcChannel;
    private string _currentSni = "";
    private X509Certificate2? _clientCert;
    private string? _jwtToken;
    private CancellationTokenSource? _rotationCts;
    public static string? DynamicSslPublicKeyHash { get; set; }
    private static readonly string[] t_legitimateHosts = [
        "google.com",
        "drive.google.com",
        "play.google.com",
        "meet.google.com",
        "docs.google.com",
        "mail.google.com"
    ];
    private static readonly string[] t_decoyPaths = [
        "/",
        "/robots.txt",
        "/favicon.ico",
        "/sitemap.xml",
        "/terms",
        "/privacy"
    ];
    private static readonly string[] t_userAgents = [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15",
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Mobile/15E148 Safari/604.1",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0"
    ];
    private static readonly HttpClient t_decoyClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly Stream?[] _tunnelStreams = new Stream?[PacketRouter.MaxRays];
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<(string, string)>? _ipTcs;
    private readonly Channel<(byte[] buffer, int length)>?[] _txChannels = new Channel<(byte[], int)>?[PacketRouter.MaxRays];

    public int ActiveRays { get; private set; } = 1;

    public bool IsConnected => _grpcChannel is not null;
    public string AssignedIp { get; private set; } = "10.8.0.2";
    public string AssignedIpV6 { get; private set; } = "fd00::2";
    public event Action<byte[], int>? OnPacketReceived;
    public event Action? OnConnectionDropped;
    public event Action? OnDeadConnectionDetected;
    public event Action<long, long>? OnTrafficUpdated;
    public static event Action<string>? OnCertificateRevoked;
    private long _totalBytesSent;
    private long _totalBytesReceived;
    public long TotalBytesSent => Interlocked.Read(ref _totalBytesSent);
    public long TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);
    public async Task ConnectRayAsync(int i, bool isNewConnection = false)
    {
        var client = new TunnelService.TunnelServiceClient(_grpcChannel);
        var headers = new Metadata
        {
            { "X-Obxodka-Auth", _clientCert!.Thumbprint },
            { "X-Active-Rays", ActiveRays.ToString(CultureInfo.InvariantCulture) },
            { "X-Ray-Index", i.ToString(CultureInfo.InvariantCulture) }
        };
        if (!string.IsNullOrEmpty(_jwtToken))
        {
            headers.Add("X-Obxodka-Token", _jwtToken);
        }
        var call = client.ConnectStream(headers, cancellationToken: _cts!.Token);
        var dummy = new byte[16];
        dummy[0] = 16;
        dummy[1] = (byte)i;
        dummy[2] = (byte)(isNewConnection ? 1 : 0);
        var packet = new TunnelPacket { Data = Google.Protobuf.ByteString.CopyFrom(dummy) };
        await call.RequestStream.WriteAsync(packet, _cts!.Token);
        var grpcStream = new TunnelGrpcStream(call);
        Volatile.Write(ref _tunnelStreams[i], grpcStream);
    }
    public async Task ConnectAsync(string serverIp, int serverPort)
    {
        if (IsConnected)
        {
            return;
        }
        var session = await AuthManager.LoadSessionAsync();
        if (string.IsNullOrEmpty(session.VpnConfig))
        {
            throw new InvalidOperationException("Сертификат VPN отсутствует. Авторизуйтесь заново.");
        }
        var certBytes = Convert.FromBase64String(session.VpnConfig);
        try
        {
            _clientCert = X509CertificateLoader.LoadPkcs12(certBytes, AppSecrets.InternalPfxPassword, X509KeyStorageFlags.DefaultKeySet);
        }
        catch
        {
            Debug.WriteLine("[VPN] Обнаружен устаревший сертификат. Требуется повторная авторизация.");
            OnCertificateRevoked?.Invoke("Обнаружен устаревший сертификат. Пожалуйста, войдите снова.");
            throw new UnauthorizedAccessException("Old certificate");
        }
        _jwtToken = session.JwtToken;
        _cts = new CancellationTokenSource();
        _currentSni = t_legitimateHosts[Random.Shared.Next(t_legitimateHosts.Length)];
        Debug.WriteLine($"[SNI MASKING] Spoofing SNI as: {_currentSni}");
        Debug.WriteLine($"[GRPC CONNECT] Connecting via gRPC on port {serverPort}...");
#if ANDROID || IOS
        ActiveRays = Preferences.Get("BatteryMode", 2);
#else
        ActiveRays = PacketRouter.MaxRays;
#endif
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = _currentSni,
                ClientCertificates = [_clientCert],
                RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
                    ValidateServerCertificate(certificate as X509Certificate2, chain, errors)
            },
            InitialHttp2StreamWindowSize = 10485760
        };

        var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var useHttp3 = Preferences.Get("UseHttp3", false);
        if (useHttp3)
        {
            httpClient.DefaultRequestVersion = HttpVersion.Version30;
            httpClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        var channelOptions = new GrpcChannelOptions
        {
            HttpClient = httpClient,
            MaxReceiveMessageSize = null,
            MaxSendMessageSize = null,
            DisposeHttpClient = true
        };
        _grpcChannel = GrpcChannel.ForAddress($"https://{serverIp}:{serverPort}", channelOptions);
        try
        {
            for (var i = 0; i < ActiveRays; i++)
            {
                _txChannels[i] = Channel.CreateBounded<(byte[], int)>(new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.Wait });
                await ConnectRayAsync(i, isNewConnection: i == 0);
                _ = TxLoopAsync(i, _txChannels[i]!, _cts!.Token);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GRPC FAIL] {ex.Message}");
            try
            { _grpcChannel?.Dispose(); }
            catch { }
            _grpcChannel = null;
            _cts?.Cancel();
            throw new InvalidOperationException($"Failed to connect via gRPC tunnel: {ex.Message} {ex.InnerException?.Message}", ex);
        }
        _ipTcs = new TaskCompletionSource<(string, string)>();
        _ = Task.Run(async () =>
        {
            var tasks = Enumerable.Range(0, ActiveRays).Select(i => ReceiveLoopAsync(i, _cts!.Token)).ToArray();
            await Task.WhenAll(tasks);
        }, _cts!.Token);
        _rotationCts = CancellationTokenSource.CreateLinkedTokenSource(_cts!.Token);
        _ = DecoyLoopAsync(_rotationCts.Token);
        try
        {
            var (ip, ip6) = await _ipTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            AssignedIp = ip;
            AssignedIpV6 = ip6;
            Debug.WriteLine($"[ENGINE] Got IP={AssignedIp}, IP6={AssignedIpV6}");
        }
        catch (TimeoutException)
        {
            try
            { _grpcChannel?.Dispose(); }
            catch { }
            _grpcChannel = null;
            _cts?.Cancel();
            throw new InvalidOperationException("Timed out waiting for IP assignment from server.");
        }
        _ = Task.Run(async () =>
        {
            long lastSent = 0;
            long lastReceived = 0;
            var deadSeconds = 0;
            try
            {
                while (!_cts!.Token.IsCancellationRequested)
                {
                    var currentSent = TotalBytesSent;
                    var currentReceived = TotalBytesReceived;
                    OnTrafficUpdated?.Invoke(currentSent, currentReceived);

                    if (currentSent > lastSent && currentReceived == lastReceived)
                    {
                        deadSeconds++;
                        if (deadSeconds >= 30)
                        {
                            Debug.WriteLine("[ENGINE] Dead connection detected (30s without RX while TX).");
                            OnDeadConnectionDetected?.Invoke();
                            break;
                        }
                    }
                    else if (currentReceived > lastReceived)
                    {
                        deadSeconds = 0;
                    }

                    lastSent = currentSent;
                    lastReceived = currentReceived;
                    await Task.Delay(1000, _cts!.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
    }
    private async Task ReceiveLoopAsync(int rayIndex, CancellationToken ct)
    {
        var header = new byte[8];
        while (!ct.IsCancellationRequested)
        {
            var stream = Volatile.Read(ref _tunnelStreams[rayIndex]);
            if (stream == null)
            {
                await Task.Delay(1000, ct);
                continue;
            }
            try
            {
                await stream.ReadExactlyAsync(header, ct);
                var totalLen = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);
                var realLen = header[4] | (header[5] << 8) | (header[6] << 16) | (header[7] << 24);

                if (totalLen is <= 0 or > 1048576 || realLen > totalLen)
                {
                    throw new InvalidDataException($"Invalid packet: totalLen={totalLen}, realLen={realLen}");
                }

                var packet = ArrayPool<byte>.Shared.Rent(realLen);
                await stream.ReadExactlyAsync(packet.AsMemory(0, realLen), ct);
                _ = Interlocked.Add(ref _totalBytesReceived, totalLen);

                var paddingLen = totalLen - 8 - realLen;
                if (paddingLen > 0)
                {
                    var trash = ArrayPool<byte>.Shared.Rent(paddingLen);
                    await stream.ReadExactlyAsync(trash.AsMemory(0, paddingLen), ct);
                    ArrayPool<byte>.Shared.Return(trash);
                }

                if (realLen > 0)
                {
                    if (realLen >= 3 && packet[0] == 'I' && packet[1] == 'P' && packet[2] == ':')
                    {
                        var msg = Encoding.UTF8.GetString(packet, 0, realLen);
                        ArrayPool<byte>.Shared.Return(packet);
                        string ip = "", ip6 = "";
                        foreach (var part in msg.Split('|'))
                        {
                            if (part.StartsWith("IP:", StringComparison.Ordinal))
                            {
                                ip = part[3..].Trim();
                            }
                            else if (part.StartsWith("IP6:", StringComparison.Ordinal))
                            {
                                ip6 = part[4..].Trim();
                            }
                        }
                        _ = (_ipTcs?.TrySetResult((ip, ip6)));
                    }
                    else
                    {
                        OnPacketReceived?.Invoke(packet, realLen);
                    }
                }
                else
                {
                    ArrayPool<byte>.Shared.Return(packet);
                }
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    if (ex is RpcException rpcEx && rpcEx.StatusCode == StatusCode.Unauthenticated)
                    {
                        Debug.WriteLine($"[TUNNEL DROP] Ray {rayIndex}: Certificate Revoked! Aborting reconnect.");
                        Volatile.Write(ref _tunnelStreams[rayIndex], null);
                        try
                        { stream.Dispose(); }
                        catch { }
                        OnCertificateRevoked?.Invoke("Обнаружен устаревший сертификат. Пожалуйста, войдите снова.");
                        break;
                    }

                    Debug.WriteLine($"[TUNNEL DROP] Ray {rayIndex}: {ex.Message}");
                    Volatile.Write(ref _tunnelStreams[rayIndex], null);
                    try
                    { stream.Dispose(); }
                    catch { }
                    for (var retry = 0; retry < 10 && !ct.IsCancellationRequested; retry++)
                    {
                        try
                        {
                            await Task.Delay(1000, ct);
                            await ConnectRayAsync(rayIndex);
                            Debug.WriteLine($"[TUNNEL RECONNECT] Ray {rayIndex} reconnected successfully.");
                            break;
                        }
                        catch (Exception e)
                        {
                            Debug.WriteLine($"[TUNNEL RECONNECT] Ray {rayIndex} failed: {e.Message}");
                        }
                    }
                    if (Volatile.Read(ref _tunnelStreams[rayIndex]) == null)
                    {
                        var isActive = false;
                        for (var i = 0; i < PacketRouter.MaxRays; i++)
                        {
                            if (Volatile.Read(ref _tunnelStreams[i]) != null)
                            {
                                isActive = true;
                            }
                        }
                        if (!isActive)
                        {
                            OnConnectionDropped?.Invoke();
                            break;
                        }
                    }
                }
            }
        }
    }
    public Task SendPacketAsync(byte[] packet)
    {
        if (!IsConnected)
        {
            return Task.CompletedTask;
        }
        var ray = PacketRouter.GetRayIndex(packet, packet.Length, ActiveRays) % ActiveRays;
        var channel = _txChannels[ray];
        if (channel == null)
        {
            return Task.CompletedTask;
        }
        var buffer = Obfuscator.Pack(packet, packet.Length, out var totalLength);
        if (!channel.Writer.TryWrite((buffer, totalLength)))
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return Task.CompletedTask;
    }
    public Task SendPacketFromPoolAsync(byte[] inputBuf, int length)
    {
        if (!IsConnected)
        {
            ArrayPool<byte>.Shared.Return(inputBuf);
            return Task.CompletedTask;
        }
        var ray = PacketRouter.GetRayIndex(inputBuf, length, ActiveRays) % ActiveRays;
        var channel = _txChannels[ray];
        if (channel == null)
        {
            ArrayPool<byte>.Shared.Return(inputBuf);
            return Task.CompletedTask;
        }
        var packed = Obfuscator.Pack(inputBuf, length, out var totalLength);
        ArrayPool<byte>.Shared.Return(inputBuf);
        if (!channel.Writer.TryWrite((packed, totalLength)))
        {
            ArrayPool<byte>.Shared.Return(packed);
        }
        return Task.CompletedTask;
    }
    private async Task TxLoopAsync(int rayIndex, Channel<(byte[] buffer, int length)> channel, CancellationToken ct)
    {
        try
        {
            var batchBuffer = new byte[524288];
            while (!ct.IsCancellationRequested)
            {
                var (buffer, length) = await channel.Reader.ReadAsync(ct);
                var offset = 0;
                Buffer.BlockCopy(buffer, 0, batchBuffer, offset, length);
                offset += length;
                ArrayPool<byte>.Shared.Return(buffer);
                while (offset < 16384 && channel.Reader.TryRead(out var nextPkt))
                {
                    Buffer.BlockCopy(nextPkt.buffer, 0, batchBuffer, offset, nextPkt.length);
                    offset += nextPkt.length;
                    ArrayPool<byte>.Shared.Return(nextPkt.buffer);
                }
                var stream = Volatile.Read(ref _tunnelStreams[rayIndex]);
                if (stream != null)
                {
                    try
                    {
                        await stream.WriteAsync(batchBuffer.AsMemory(0, offset), ct);
                        _ = Interlocked.Add(ref _totalBytesSent, offset);
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TX ERROR] {ex.Message}");
        }
    }
    private async Task DecoyLoopAsync(CancellationToken ct)
    {
        try
        { await Task.Delay(2000, ct); }
        catch { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!string.IsNullOrEmpty(_currentSni))
                {
                    var path = t_decoyPaths[Random.Shared.Next(t_decoyPaths.Length)];
                    var userAgent = t_userAgents[Random.Shared.Next(t_userAgents.Length)];
                    var uri = $"https://{_currentSni}{path}";
                    Debug.WriteLine($"[DECOY] Sending background traffic to {uri}");
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    request.Headers.Add("User-Agent", userAgent);
                    request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
                    request.Headers.Add("Accept-Language", "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
                    using var response = await t_decoyClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (response.Content.Headers.ContentLength > 5 * 1024 * 1024)
                    {
                        Debug.WriteLine($"[DECOY WARNING] Response too large, skipping.");
                        continue;
                    }
                    var content = await response.Content.ReadAsByteArrayAsync(ct);
                    if (response.IsSuccessStatusCode)
                    {
                        Debug.WriteLine($"[DECOY SUCCESS] Successfully downloaded {content.Length} bytes from {uri} (Status: {(int)response.StatusCode})");
                    }
                    else
                    {
                        Debug.WriteLine($"[DECOY WARNING] Downloaded {content.Length} bytes from {uri}, but server returned status: {(int)response.StatusCode} {response.ReasonPhrase}");
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DECOY ERROR] Failed to send traffic: {ex.Message}");
            }
            var delayMinutes = Random.Shared.Next(1, 4);
            try
            { await Task.Delay(TimeSpan.FromMinutes(delayMinutes), ct); }
            catch { break; }
        }
    }
    public async ValueTask DisposeAsync()
    {

        _cts?.Cancel();
        _rotationCts?.Cancel();
        _clientCert?.Dispose();
        if (_grpcChannel != null)
        {
            try
            {
                _ = await Task.WhenAny(_grpcChannel.ShutdownAsync(), Task.Delay(500));
            }
            catch { }
            try
            { _grpcChannel.Dispose(); }
            catch { }
            _grpcChannel = null;
        }
        for (var i = 0; i < PacketRouter.MaxRays; i++)
        {
            if (_tunnelStreams[i] != null)
            {
                try
                {
                    var disposeTask = _tunnelStreams[i]!.DisposeAsync().AsTask();
                    _ = await Task.WhenAny(disposeTask, Task.Delay(200));
                }
                catch { }
                _tunnelStreams[i] = null;
            }
        }
    }
    private bool ValidateServerCertificate(
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors _)
    {
        if (certificate == null)
        {
            return false;
        }

        var expectedHash = !string.IsNullOrEmpty(DynamicSslPublicKeyHash)
            ? DynamicSslPublicKeyHash
            : AppSecrets.SslPublicKeyHash;

        if (!string.IsNullOrEmpty(expectedHash))
        {
            var hash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(certificate.GetPublicKey()));
            if (hash == expectedHash)
            {
                return true;
            }
            Debug.WriteLine($"[SSL] PINNING FAILED! Сервер отдаёт: {hash} Клиент ожидает: {expectedHash}. Блокировка соединения!");
            return false;
        }

        var cn = certificate.GetNameInfo(X509NameType.DnsName, false) ?? "";
        var expectedCn = _currentSni;
        var apiHost = "";
        try
        { apiHost = new Uri(AppConfig.ApiBaseUrl).Host; }
        catch { }

        static bool MatchHost(string host, string pattern)
        {
            if (string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (pattern.StartsWith("*.", StringComparison.Ordinal) && host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase))
            {
                var prefix = host[..^pattern[1..].Length];
                return !prefix.Contains('.');
            }
            return false;
        }

        if (!MatchHost(expectedCn, cn) && !string.IsNullOrEmpty(apiHost) && !MatchHost(apiHost, cn))
        {
            Debug.WriteLine($"[SSL] CN mismatch: expected {expectedCn} or {apiHost}, got {cn}");
            return false;
        }

        if (DateTime.UtcNow > certificate.NotAfter || DateTime.UtcNow < certificate.NotBefore)
        {
            Debug.WriteLine($"[SSL] Certificate expired or not yet valid");
            return false;
        }

        if (chain != null && chain.ChainStatus.Any(s =>
            s.Status is X509ChainStatusFlags.Revoked or X509ChainStatusFlags.NotTimeValid or X509ChainStatusFlags.NotSignatureValid))
        {
            Debug.WriteLine($"[SSL] Certificate chain invalid");
            return false;
        }

        return true;
    }
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _rotationCts?.Cancel();
        _rotationCts?.Dispose();
        _clientCert?.Dispose();
        try
        {
            if (_grpcChannel != null)
            {
                _ = _grpcChannel.ShutdownAsync();
            }
        }
        catch { }
        try
        {
            _grpcChannel?.Dispose();
        }
        catch { }
        for (var i = 0; i < PacketRouter.MaxRays; i++)
        {
            _tunnelStreams[i]?.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
