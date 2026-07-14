namespace obxodka.Core;

internal sealed partial class OctopusEngine : IDisposable, IAsyncDisposable
{
    private static readonly Lazy<OctopusEngine> t_instance = new(
        () => new OctopusEngine(),
        LazyThreadSafetyMode.ExecutionAndPublication
    );
    public static OctopusEngine Current => t_instance.Value;
    public static bool IsQuicAvailable { get; private set; } = true;
    private QuicConnection? _connection;
    private GrpcChannel? _grpcChannel;
    private string _serverIp = "";
    private int _serverPort = 443;
    private string _currentSni = "";
    private X509Certificate2? _clientCert;
    private CancellationTokenSource? _rotationCts;
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
    private static readonly HttpClient t_decoyClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    private readonly Stream?[] _tunnelStreams = new Stream?[PacketRouter.MaxRays];
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<(string, string)>? _ipTcs;
    private readonly Channel<(byte[] buffer, int length)>?[] _txChannels = new Channel<(byte[], int)>?[PacketRouter.MaxRays];
    private int _activeRays = 1;
    public bool IsConnected => _connection is not null || _grpcChannel is not null;
    public string AssignedIp { get; private set; } = "10.8.0.2";
    public string AssignedIpV6 { get; private set; } = "fd00::2";
    public event Action<byte[]>? OnPacketReceived;
    public event Action? OnConnectionDropped;
    public event Action<long, long>? OnTrafficUpdated;
    private long _totalBytesSent;
    private long _totalBytesReceived;
    public long TotalBytesSent => Interlocked.Read(ref _totalBytesSent);
    public long TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);

    public async Task ConnectAsync(string serverIp, int serverPort)
    {
        if (IsConnected)
        {
            return;
        }

        var session = await AuthManager.LoadSessionAsync();
        if (string.IsNullOrEmpty(session.VpnConfig))
        {
            throw new InvalidOperationException("Отсутствует VPN сертификат. Перезайдите в аккаунт.");
        }

        var certBytes = Convert.FromBase64String(session.VpnConfig);
        _clientCert = X509CertificateLoader.LoadPkcs12(certBytes, "obxodka_internal_pass", X509KeyStorageFlags.DefaultKeySet);
        _cts = new CancellationTokenSource();
        _serverIp = serverIp;
        _serverPort = serverPort;

        _currentSni = t_legitimateHosts[Random.Shared.Next(t_legitimateHosts.Length)];
        Debug.WriteLine($"[SNI MASKING] Spoofing SNI as: {_currentSni}");

        var options = new QuicClientConnectionOptions
        {
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,
            MaxInboundBidirectionalStreams = 1000,
            MaxInboundUnidirectionalStreams = 1000,
            IdleTimeout = TimeSpan.FromMinutes(2),
            KeepAliveInterval = TimeSpan.FromSeconds(15),
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse(serverIp), serverPort),
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = _currentSni,
                ApplicationProtocols = [new SslApplicationProtocol("h3")],
                ClientCertificates = [_clientCert],
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
                    ValidateServerCertificate(certificate as X509Certificate2, chain, errors)
            }
        };

        var quicSuccess = false;
        if (IsQuicAvailable && QuicConnection.IsSupported)
        {
            try
            {
                using var quicCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                quicCts.CancelAfter(TimeSpan.FromMilliseconds(1500));
                _connection = await QuicConnection.ConnectAsync(options, quicCts.Token);
                _activeRays = PacketRouter.MaxRays;
                for (var i = 0; i < PacketRouter.MaxRays; i++)
                {
                    var stream = await _connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, quicCts.Token);
                    _tunnelStreams[i] = stream;
                    _txChannels[i] = Channel.CreateBounded<(byte[], int)>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.Wait });
                    var dummy = new byte[8];
                    dummy[0] = 8;
                    await stream.WriteAsync(dummy, _cts.Token);
                    _ = TxLoopAsync(i, _txChannels[i]!, _cts.Token);
                }
                quicSuccess = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[QUIC FAIL] {ex.Message}");
            }
        }

        if (!quicSuccess)
        {
            Debug.WriteLine($"[GRPC FALLBACK] Trying gRPC on port 443...");
            _activeRays = 1;

            var handler = new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = _currentSni,
                    ClientCertificates = new X509CertificateCollection { _clientCert },
                    RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
                        ValidateServerCertificate(certificate as X509Certificate2, chain, errors)
                }
            };
            
            _grpcChannel = GrpcChannel.ForAddress($"https://{serverIp}:443", new GrpcChannelOptions 
            { 
                HttpHandler = handler 
            });

            var client = new TunnelService.TunnelServiceClient(_grpcChannel);
            var headers = new Grpc.Core.Metadata
            {
                { "X-Obxodka-Auth", _clientCert.Thumbprint }
            };
            
            var call = client.ConnectStream(headers);

            try
            {
                var dummy = new byte[16];
                dummy[0] = 16;
                var packet = new TunnelPacket { Data = Google.Protobuf.ByteString.CopyFrom(dummy) };
                await call.RequestStream.WriteAsync(packet, _cts.Token);

                var grpcStream = new TunnelGrpcStream(call);
                _tunnelStreams[0] = grpcStream;
                _txChannels[0] = Channel.CreateBounded<(byte[], int)>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.Wait });
                
                _ = TxLoopAsync(0, _txChannels[0]!, _cts.Token);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GRPC FAIL] {ex.Message}");
                throw new InvalidOperationException("Failed to connect via both QUIC and gRPC fallback.", ex);
            }
        }

        _ipTcs = new TaskCompletionSource<(string, string)>();
        _ = Task.Run(async () =>
        {
            var tasks = _tunnelStreams.Where(s => s != null).Select(s => ReceiveLoopAsync(s!, _cts.Token)).ToArray();
            await Task.WhenAll(tasks);
        }, _cts.Token);

        if (quicSuccess)
        {
            _rotationCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _ = DecoyLoopAsync(_rotationCts.Token);
        }

        try
        {
            var (ip, ip6) = await _ipTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            AssignedIp = ip;
            AssignedIpV6 = ip6;
            Debug.WriteLine($"[ENGINE] Got IP={AssignedIp}, IP6={AssignedIpV6}");
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("Timed out waiting for IP assignment from server.");
        }
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    OnTrafficUpdated?.Invoke(TotalBytesSent, TotalBytesReceived);
                    await Task.Delay(1000, _cts.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private async Task ReceiveLoopAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[8];
        while (!ct.IsCancellationRequested)
        {
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
                        var exactPacket = new byte[realLen];
                        Buffer.BlockCopy(packet, 0, exactPacket, 0, realLen);
                        ArrayPool<byte>.Shared.Return(packet);
                        OnPacketReceived?.Invoke(exactPacket);
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
                    var isActive = false;
                    for (int i = 0; i < PacketRouter.MaxRays; i++)
                    {
                        if (Volatile.Read(ref _tunnelStreams[i]) == stream)
                            isActive = true;
                    }
                    if (isActive)
                    {
                        Debug.WriteLine($"[QUIC MAIN DROP] {ex.Message}");
                        OnConnectionDropped?.Invoke();
                    }
                }
                break;
            }
        }
    }

    public Task SendPacketAsync(byte[] packet)
    {
        if (!IsConnected)
        {
            return Task.CompletedTask;
        }

        var ray = PacketRouter.GetRayIndex(packet, packet.Length) % _activeRays;
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
        var ray = PacketRouter.GetRayIndex(inputBuf, length) % _activeRays;
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
                while (offset < 262144 && channel.Reader.TryRead(out var nextPkt))
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
            Debug.WriteLine($"[QUIC TX ERROR] {ex.Message}");
        }
    }

    public async Task RelayStreamAsync(Stream localStream, string target, CancellationToken ct)
    {
        if (!IsConnected)
        {
            return;
        }

        if (_connection != null)
        {
            await RelayQuicStreamAsync(localStream, target, ct);
        }
    }

    private async Task RelayQuicStreamAsync(Stream localStream, string target, CancellationToken ct)
    {
        if (_connection == null)
        {
            return;
        }

        try
        {
            using var quicStream = await _connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct);
            var header = Encoding.UTF8.GetBytes(target + "\n");
            var buffer = ArrayPool<byte>.Shared.Rent(header.Length + 1);
            try
            {
                buffer[0] = 0x02;
                Buffer.BlockCopy(header, 0, buffer, 1, header.Length);
                await quicStream.WriteAsync(buffer.AsMemory(0, header.Length + 1), ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            var upload = OctopusProtocol.PumpTrafficAsync(localStream, quicStream, ct);
            var download = OctopusProtocol.PumpTrafficAsync(quicStream, localStream, ct);
            _ = await Task.WhenAny(upload, download);
            try
            { quicStream.CompleteWrites(); }
            catch { }
            try
            { localStream.Close(); }
            catch { }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[QUIC RELAY ERROR] {ex.Message}");
        }
    }


    private async Task DecoyLoopAsync(CancellationToken ct)
    {
        // Отправляем первый запрос сразу же после запуска (пауза 2 секунды, чтобы сеть успела подняться)
        try { await Task.Delay(2000, ct); } catch { return; }
        
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
                    
                    using var response = await t_decoyClient.SendAsync(request, ct);
                    
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
            try { await Task.Delay(TimeSpan.FromMinutes(delayMinutes), ct); } catch { break; }
        }
    }



    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _rotationCts?.Cancel();
        _clientCert?.Dispose();
        if (_connection != null)
        {
            try
            {
                await _connection.CloseAsync(0);
                await _connection.DisposeAsync();
            }
            catch { }
            _connection = null;
        }
        if (_grpcChannel != null)
        {
            try { await _grpcChannel.ShutdownAsync(); } catch { }
            try { _grpcChannel.Dispose(); } catch { }
            _grpcChannel = null;
        }
        for (var i = 0; i < PacketRouter.MaxRays; i++)
        {
            if (_tunnelStreams[i] != null)
            {
                await _tunnelStreams[i]!.DisposeAsync();
                _tunnelStreams[i] = null;
            }
        }
    }

    private static bool ValidateServerCertificate(
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors _)
    {
        if (certificate == null)
        {
            return false;
        }

        var cn = certificate.GetNameInfo(X509NameType.DnsName, false);
        const string expectedCn = "google.com";
        if (string.IsNullOrEmpty(cn) || !cn.Equals(expectedCn, StringComparison.OrdinalIgnoreCase))
        {
            Debug.WriteLine($"[SSL] CN mismatch: expected {expectedCn}, got {cn}");
            return false;
        }
        if (DateTime.UtcNow > certificate.NotAfter || DateTime.UtcNow < certificate.NotBefore)
        {
            Debug.WriteLine($"[SSL] Certificate expired or not yet valid");
            return false;
        }
        if (chain != null && chain.ChainStatus.Any(s =>
            s.Status is X509ChainStatusFlags.Revoked or
            X509ChainStatusFlags.NotValidForUsage))
        {
            Debug.WriteLine($"[SSL] Chain validation failed");
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
        _connection?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (_grpcChannel != null)
        {
            try { _grpcChannel.ShutdownAsync().GetAwaiter().GetResult(); } catch { }
            try { _grpcChannel.Dispose(); } catch { }
        }
        for (var i = 0; i < PacketRouter.MaxRays; i++)
        {
            _tunnelStreams[i]?.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
