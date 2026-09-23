using System.Security.Authentication;

namespace obxodka.Core.Transports;

public sealed partial class GrpcTransport(
    bool useHttp3,
    int activeRays,
    X509Certificate2? clientCert,
    string? jwtToken = null,
    int serverPort = 443,
    MeshRelayInfo? meshRelay = null,
    string? targetSni = null) : IVpnTransport
{
    private readonly bool _useHttp3 = useHttp3;
    private readonly int _activeRays = Math.Clamp(activeRays, 1, PacketRouter.MaxRays);
    private readonly string? _certThumbprint = clientCert?.Thumbprint;
    private readonly string? _jwtToken = jwtToken;
    private readonly int _serverPort = serverPort > 0 ? serverPort : 443;
    private readonly MeshRelayInfo? _meshRelay = meshRelay;
    private readonly string? _configuredSni = targetSni;
    private string _thumbprint = string.Empty;

    private readonly GrpcChannel?[] _grpcChannels = new GrpcChannel?[PacketRouter.MaxRays];
    private readonly PriorityPacketQueue?[] _txChannels = new PriorityPacketQueue?[PacketRouter.MaxRays];
    private readonly Stream?[] _tunnelStreams = new Stream?[PacketRouter.MaxRays];
    private readonly PacketDeduplicator _deduplicator = new();
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<(string, string)>? _ipTcs;

    public string ProtocolName => _useHttp3 ? "HTTP3" : "HTTP2";
    public bool IsConnected => Array.Exists(_grpcChannels, c => c is not null);

    public event Action<byte[], int>? OnPacketReceived;
    public event Action<long>? OnPingUpdated;
    public event Action? OnConnectionDropped;

    public static Action<Socket>? OnSocketCreated { get; set; }

    public void ProtectSockets(Action<Socket> protectAction)
    {
    }

    public static bool ValidateServerCertificate(
        X509Certificate? certificate,
        X509Chain? chain = null,
        SslPolicyErrors errors = SslPolicyErrors.None,
        string? dynamicPinningHash = null)
    {
        if (certificate is null)
        {
            return false;
        }

        try
        {
            using var cert2 = certificate as X509Certificate2 ?? new X509Certificate2(certificate);

            var nowUtc = DateTime.UtcNow;
            var notBeforeUtc = cert2.NotBefore.ToUniversalTime();
            var notAfterUtc = cert2.NotAfter.ToUniversalTime();

            if (nowUtc < notBeforeUtc - TimeSpan.FromDays(1) || nowUtc > notAfterUtc)
            {
                return false;
            }

            var expectedPin = !string.IsNullOrWhiteSpace(dynamicPinningHash)
                ? dynamicPinningHash
                : OctopusEngine.DynamicSslPublicKeyHash;

            if (!string.IsNullOrWhiteSpace(expectedPin))
            {
                var pubKey = cert2.GetPublicKey();
                var hash = Convert.ToBase64String(SHA256.HashData(pubKey));
                if (string.Equals(hash, expectedPin, StringComparison.Ordinal))
                {
                    return true;
                }

                var key = cert2.GetRSAPublicKey() as AsymmetricAlgorithm ?? cert2.GetECDsaPublicKey();
                if (key is not null)
                {
                    var spkiHash = Convert.ToBase64String(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
                    if (string.Equals(spkiHash, expectedPin, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            var isObxodkaDomain = cert2.Subject.Contains("obxodka.one", StringComparison.OrdinalIgnoreCase);
            if (!isObxodkaDomain)
            {
                foreach (var ext in cert2.Extensions)
                {
                    if (ext is X509SubjectAlternativeNameExtension sanExt)
                    {
                        foreach (var dns in sanExt.EnumerateDnsNames())
                        {
                            if (dns.Equals("obxodka.one", StringComparison.OrdinalIgnoreCase) ||
                                dns.EndsWith(".obxodka.one", StringComparison.OrdinalIgnoreCase))
                            {
                                isObxodkaDomain = true;
                                break;
                            }
                        }
                    }
                    if (isObxodkaDomain)
                    {
                        break;
                    }
                }
            }

            if (isObxodkaDomain)
            {

                var nonNameErrors = errors & ~SslPolicyErrors.RemoteCertificateNameMismatch;
                if (nonNameErrors == SslPolicyErrors.None)
                {
                    using var verifyChain = new X509Chain();
                    verifyChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    if (verifyChain.Build(cert2))
                    {
                        return true;
                    }

                    if (chain is not null)
                    {
                        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        if (chain.Build(cert2))
                        {
                            return true;
                        }
                    }
                }
            }

            if (errors == SslPolicyErrors.None && string.IsNullOrWhiteSpace(expectedPin))
            {
                using var verifyChain = new X509Chain();
                verifyChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                if (verifyChain.Build(cert2))
                {
                    return true;
                }

                if (chain is not null)
                {
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    if (chain.Build(cert2))
                    {
                        return true;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(expectedPin))
            {
                var pubKey = cert2.GetPublicKey();
                var hash = Convert.ToBase64String(SHA256.HashData(pubKey));
                Debug.WriteLine($"[CERT PINNING MISMATCH] Expected: {expectedPin}, Actual: {hash}, Errors: {errors}");
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public async Task<(string ip, string ip6)> ConnectAsync(string serverIp, string thumbprint, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ipTcs = new TaskCompletionSource<(string, string)>();
        _thumbprint = thumbprint;
        var serverPort = _serverPort;
        var defaultSni = "obxodka.one";
        try
        {
            if (Uri.TryCreate(Config.AppConfig.ApiBaseUrl, UriKind.Absolute, out var apiUri) &&
                !string.IsNullOrWhiteSpace(apiUri.Host) &&
                !IPAddress.TryParse(apiUri.Host, out _))
            {
                defaultSni = apiUri.Host.EndsWith("obxodka.one", StringComparison.OrdinalIgnoreCase)
                    ? "obxodka.one"
                    : apiUri.Host;
            }
        }
        catch { }

        var targetHost = !string.IsNullOrWhiteSpace(_configuredSni) && !IPAddress.TryParse(_configuredSni, out _)
            ? _configuredSni
            : (!IPAddress.TryParse(serverIp, out _) ? serverIp : defaultSni);

        try
        {
            for (var i = 0; i < _activeRays; i++)
            {
                var handler = new SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true,
                    PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                    KeepAlivePingDelay = TimeSpan.FromSeconds(10),
                    KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
                    KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        TargetHost = targetHost,
                        ApplicationProtocols = [SslApplicationProtocol.Http2],
                        EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
                        RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
                            ValidateServerCertificate(certificate, chain, errors)
                    }
                };

                if (!_useHttp3)
                {
                    handler.ConnectCallback = async (context, cToken) =>
                    {
                        if (_meshRelay is not null)
                        {
                            return await MeshRelayClient.ConnectThroughRelayAsync(
                                _meshRelay.IpAddress,
                                _meshRelay.Port,
                                context.DnsEndPoint.Host,
                                context.DnsEndPoint.Port,
                                _jwtToken,
                                _meshRelay.IsFriend,
                                cToken
                            ).ConfigureAwait(false);
                        }

                        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
                        {
                            NoDelay = true,
                            SendBufferSize = 8388608,
                            ReceiveBufferSize = 8388608
                        };
                        try
                        {
                            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 0x2E);
                        }
                        catch { }
                        OnSocketCreated?.Invoke(socket);
                        try
                        {
                            var connectTarget = IPAddress.TryParse(serverIp, out var ipAddr)
                                ? (EndPoint)new IPEndPoint(ipAddr, serverPort)
                                : context.DnsEndPoint;
                            Debug.WriteLine($"[GRPC-CONNECT] Connecting TCP socket to {connectTarget}...");
                            await socket.ConnectAsync(connectTarget, cToken).ConfigureAwait(false);
                            Debug.WriteLine($"[GRPC-CONNECT] Successfully connected TCP socket to {connectTarget}!");
                            return new DpiBypassStream(new NetworkStream(socket, ownsSocket: true), splitPosition: 2, delayMs: 25);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[GRPC-CONNECT ERROR] Failed TCP socket connect to {serverIp}:{serverPort}: {ex.Message}");
                            socket.Dispose();
                            throw;
                        }
                    };
                }

                var httpClient = new HttpClient(handler)
                {
                    Timeout = Timeout.InfiniteTimeSpan
                };

                if (_useHttp3)
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

                var channelHost = !string.IsNullOrWhiteSpace(targetHost) ? targetHost : serverIp;
                Debug.WriteLine($"[GRPC-INIT] Creating channel for ray #{i} -> https://{channelHost}:{serverPort} (Target IP: {serverIp})");
                _grpcChannels[i] = GrpcChannel.ForAddress($"https://{channelHost}:{serverPort}", channelOptions);
                _txChannels[i] = new PriorityPacketQueue(i <= 1 ? 2000 : 1500);
            }

            await ConnectRayAsync(0, isNewConnection: true).ConfigureAwait(false);
            _ = TxLoopAsync(0, _txChannels[0]!, _cts.Token);

            if (_activeRays > 1)
            {
                var secondaryRayTasks = new Task[_activeRays - 1];
                for (var i = 1; i < _activeRays; i++)
                {
                    var rayIndex = i;
                    secondaryRayTasks[i - 1] = Task.Run(async () =>
                    {
                        try
                        {
                            await ConnectRayAsync(rayIndex, isNewConnection: false).ConfigureAwait(false);
                            _ = TxLoopAsync(rayIndex, _txChannels[rayIndex]!, _cts.Token);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[GRPC-RAY #{rayIndex} WARN] Secondary ray failed: {ex.Message}");
                        }
                    }, _cts.Token);
                }

                try
                {
                    _ = await Task.WhenAny(Task.WhenAll(secondaryRayTasks), Task.Delay(3500, _cts.Token)).ConfigureAwait(false);
                }
                catch { }
            }
        }
        catch (OperationCanceledException)
        {
            Dispose();
            throw;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled || ct.IsCancellationRequested || _cts?.IsCancellationRequested == true)
        {
            Dispose();
            throw new OperationCanceledException("gRPC call canceled by client.", ex, ct);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GRPC FAIL] {ex.Message}");
            Dispose();
            throw new InvalidOperationException($"Failed to connect via gRPC tunnel: {ex.Message}", ex);
        }

        _ = PingLoopAsync(_cts.Token);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(15));
        using (linkedCts.Token.Register(() => _ipTcs.TrySetCanceled()))
        {
            return await _ipTcs.Task;
        }
    }

    public Task SendPingProbeAsync()
    {
        try
        {
            var ts = Stopwatch.GetTimestamp();
            var targetRays = _activeRays;
            for (var r = 0; r < targetRays; r++)
            {
                if (_txChannels[r] is { } ch && _tunnelStreams[r] != null)
                {
                    var packet = ArrayPool<byte>.Shared.Rent(9);
                    packet[0] = 0x99;
                    BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(1, 8), ts);
                    var packed = Obfuscator.Pack(packet, 9, out var totalLength);
                    ArrayPool<byte>.Shared.Return(packet);
                    if (!ch.TryEnqueue(packed, totalLength))
                    {
                        ArrayPool<byte>.Shared.Return(packed);
                    }
                    else
                    {
                        Debug.WriteLine($"[GRPC-PING-PROBE] Enqueued 0x99 ping probe to Ray #{r} (timestamp={ts})");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GRPC-PING-PROBE-ERR] {ex.Message}");
        }
        return Task.CompletedTask;
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await SendPingProbeAsync();
            await Task.Delay(1500, ct);
        }
    }

    private async Task ConnectRayAsync(int rayIndex, bool isNewConnection)
    {
        var client = new TunnelService.TunnelServiceClient(_grpcChannels[rayIndex]);
        var headers = new Metadata();

        var authThumb = !string.IsNullOrEmpty(_thumbprint) ? _thumbprint : _certThumbprint ?? "";
        if (!string.IsNullOrEmpty(authThumb))
        {
            headers.Add("X-Obxodka-Auth", authThumb);
        }

        if (!string.IsNullOrEmpty(_jwtToken))
        {
            headers.Add("X-Obxodka-Token", $"Bearer {_jwtToken}");
        }

        headers.Add("X-Active-Rays", _activeRays.ToString(CultureInfo.InvariantCulture));
        headers.Add("X-Ray-Index", rayIndex.ToString(CultureInfo.InvariantCulture));

        try
        {
            Debug.WriteLine($"[GRPC-RAY-{rayIndex}] Connecting bidirectional stream (isNew: {isNewConnection})...");
            var call = client.ConnectStream(headers, cancellationToken: _cts!.Token);
            var handshake = new byte[16];
            handshake[0] = 16;
            handshake[1] = (byte)rayIndex;
            handshake[2] = (byte)(isNewConnection ? 1 : 0);

            await call.RequestStream.WriteAsync(new TunnelPacket { Data = ByteString.CopyFrom(handshake) });
            _tunnelStreams[rayIndex] = new TunnelGrpcStream(call);
            _ = ReceiveLoopAsync(rayIndex, _cts.Token);
            Debug.WriteLine($"[GRPC-RAY-{rayIndex}] Handshake sent and receive loop started!");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GRPC-RAY-{rayIndex} ERROR] ConnectStream failed: {ex.Message}");
            throw;
        }
    }

    private async Task TxLoopAsync(int rayIndex, PriorityPacketQueue txQueue, CancellationToken ct)
    {
        var isGamingRay = rayIndex == 0 || rayIndex == (_activeRays - 1);
        var batchBuffer = ArrayPool<byte>.Shared.Rent(65536);
        long pktsSent = 0;
        long bytesSent = 0;
        try
        {
            Debug.WriteLine($"[GRPC-TX-START #{rayIndex}] TX loop initialized (isGamingRay={isGamingRay}).");
            while (!ct.IsCancellationRequested)
            {
                var (buffer, length) = await txQueue.DequeueAsync(ct).ConfigureAwait(false);
                if (length <= 0)
                {
                    continue;
                }

                var offset = 0;
                Buffer.BlockCopy(buffer, 0, batchBuffer, offset, length);
                offset += length;
                ArrayPool<byte>.Shared.Return(buffer);
                pktsSent++;

                if (!isGamingRay)
                {
                    while (offset < 32768 && txQueue.TryDequeue(out var nextPkt))
                    {
                        if (nextPkt.length > 0)
                        {
                            if (offset + nextPkt.length <= batchBuffer.Length)
                            {
                                Buffer.BlockCopy(nextPkt.buffer, 0, batchBuffer, offset, nextPkt.length);
                                offset += nextPkt.length;
                                pktsSent++;
                            }
                            ArrayPool<byte>.Shared.Return(nextPkt.buffer);
                        }
                    }
                }

                if (_tunnelStreams[rayIndex] is { } stream)
                {
                    await stream.WriteAsync(batchBuffer.AsMemory(0, offset), ct).ConfigureAwait(false);
                    bytesSent += offset;
                    if (pktsSent <= 20 || pktsSent % 100 == 0)
                    {
                        Debug.WriteLine($"[GRPC-TX-RAY#{rayIndex}] Sent frame: batch={offset}B, totalPkts={pktsSent}, totalBytes={bytesSent}");
                    }
                }
                else
                {
                    Debug.WriteLine($"[GRPC-TX-WARN-RAY#{rayIndex}] Stream is null! Packet of {offset}B dropped.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine($"[GRPC-TX-STOP #{rayIndex}] TX loop canceled.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GRPC-TX-FAIL #{rayIndex}] {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(batchBuffer);
            Debug.WriteLine($"[GRPC-TX-EXIT #{rayIndex}] TX loop exited. Lifetime stats: {pktsSent} pkts, {bytesSent} bytes.");
        }
    }

    private async Task ReceiveLoopAsync(int rayIndex, CancellationToken ct)
    {
        var header = new byte[8];
        long pktsReceived = 0;
        long bytesReceived = 0;
        try
        {
            Debug.WriteLine($"[GRPC-RX-START #{rayIndex}] Receive loop initialized.");
            while (!ct.IsCancellationRequested)
            {
                if (_tunnelStreams[rayIndex] is not { } stream)
                {
                    Debug.WriteLine($"[GRPC-RX-WARN #{rayIndex}] Stream is null! Exiting loop.");
                    break;
                }

                var (packet, realLen) = await Obfuscator.ReadPacketAsync(stream, header, ct).ConfigureAwait(false);
                if (packet is null)
                {
                    Debug.WriteLine($"[GRPC-RX-EOF #{rayIndex}] Stream closed (null packet read).");
                    break;
                }

                if (realLen > 0)
                {
                    pktsReceived++;
                    bytesReceived += realLen;

                    if (realLen >= 3 && packet[0] == 'I' && packet[1] == 'P' && packet[2] == ':')
                    {
                        var msg = Encoding.UTF8.GetString(packet, 0, realLen);
                        Debug.WriteLine($"[GRPC-AUTH] Received IP assignment from server on Ray #{rayIndex}: '{msg}'");
                        ArrayPool<byte>.Shared.Return(packet);
                        var parts = msg.Split('|');
                        var ip = parts[0].Replace("IP:", "", StringComparison.Ordinal);
                        var ip6 = parts.Length > 1 ? parts[1].Replace("IP6:", "", StringComparison.Ordinal) : "fd00::2";
                        foreach (var p in parts)
                        {
                            if (p.StartsWith("WAN:", StringComparison.Ordinal))
                            {
                                var wan = p[4..].Trim();
                                if (!string.IsNullOrEmpty(wan))
                                {
                                    OctopusEngine.Current.PublicWanIp = wan;
                                    Debug.WriteLine($"[GRPC-AUTH] Detected Public WAN IP: {wan}");
                                }
                            }
                        }
                        Debug.WriteLine($"[GRPC-AUTH] Handshake SUCCESS -> Assigned IP: {ip}, IPv6: {ip6}");
                        _ = (_ipTcs?.TrySetResult((ip, ip6)));
                    }
                    else if (packet[0] == 0x99 && realLen >= 9)
                    {
                        var sentTimestamp = BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(1, 8));
                        var elapsedMs = (Stopwatch.GetTimestamp() - sentTimestamp) * 1000.0 / Stopwatch.Frequency;
                        var rtt = (long)Math.Round(elapsedMs);
                        Debug.WriteLine($"[GRPC-PING] Received pong on ray #{rayIndex}, RTT={rtt}ms, EngineTotalRX={OctopusEngine.Current.TotalBytesReceived}B");
                        if (rtt is >= 0 and < 10000)
                        {
                            OnPingUpdated?.Invoke(Math.Max(1, rtt));
                        }
                        ArrayPool<byte>.Shared.Return(packet);
                    }
                    else
                    {
                        if (pktsReceived <= 20 || pktsReceived % 50 == 0)
                        {
                            var protoDesc = realLen >= 20 ? ((packet[0] >> 4) == 4 ? $"IPv4(proto={packet[9]})" : "IPv6") : "NonIP";
                            Debug.WriteLine($"[GRPC-RX-RAY#{rayIndex}] Packet #{pktsReceived}: {protoDesc}, len={realLen}B, totalRayBytes={bytesReceived}B");
                        }

                        if (!_deduplicator.IsDuplicate(packet, realLen))
                        {
                            OnPacketReceived?.Invoke(packet, realLen);
                        }
                        else
                        {
                            ArrayPool<byte>.Shared.Return(packet);
                        }
                    }
                }
                else
                {
                    ArrayPool<byte>.Shared.Return(packet);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine($"[GRPC-RX-STOP #{rayIndex}] Receive loop canceled.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GRPC-RX-FAIL #{rayIndex}] {ex.GetType().Name}: {ex.Message}");
            if (rayIndex == 0)
            {
                OnConnectionDropped?.Invoke();
            }
        }
        finally
        {
            Debug.WriteLine($"[GRPC-RX-EXIT #{rayIndex}] Receive loop exited. Lifetime stats: {pktsReceived} pkts, {bytesReceived} bytes.");
        }
    }

    public void SendPacketFromPool(byte[] packet, int length)
    {
        if (!IsConnected)
        {
            Debug.WriteLine($"[GRPC-TX-DROP] Cannot send {length}B: Not connected.");
            ArrayPool<byte>.Shared.Return(packet);
            return;
        }

        PacketRouter.GetRays(packet, length, _activeRays, out var primaryRay, out var secondaryRay);
        var primaryChannel = _txChannels[primaryRay];
        if (primaryChannel is null)
        {
            Debug.WriteLine($"[GRPC-TX-DROP] Primary channel #{primaryRay} is null for {length}B packet.");
            ArrayPool<byte>.Shared.Return(packet);
            return;
        }

        var packed = Obfuscator.Pack(packet, length, out var totalLength);
        ArrayPool<byte>.Shared.Return(packet);

        byte[]? dup = null;
        if (secondaryRay >= 0 && secondaryRay < _activeRays && _txChannels[secondaryRay] is not null)
        {
            dup = ArrayPool<byte>.Shared.Rent(totalLength);
            Buffer.BlockCopy(packed, 0, dup, 0, totalLength);
        }

        if (!primaryChannel.TryEnqueue(packed, totalLength))
        {
            Debug.WriteLine($"[GRPC-TX-QUEUE-FULL] Ray #{primaryRay} queue full ({primaryChannel.Count} items)! Dropping {totalLength}B frame.");
            ArrayPool<byte>.Shared.Return(packed);
        }

        if (dup is not null)
        {
            var secondaryChannel = _txChannels[secondaryRay];
            if (secondaryChannel is null || !secondaryChannel.TryEnqueue(dup, totalLength))
            {
                ArrayPool<byte>.Shared.Return(dup);
            }
        }
    }

    public async Task SendDisconnectSignalAsync()
    {
        try
        {
            var discPkt = Obfuscator.Pack("DISC"u8.ToArray(), 4, out var len);
            for (var i = 0; i < _activeRays; i++)
            {
                if (_tunnelStreams[i] is { } stream)
                {
                    try
                    {
                        await stream.WriteAsync(discPkt.AsMemory(0, len), CancellationToken.None);
                    }
                    catch { }
                }
            }
            ArrayPool<byte>.Shared.Return(discPkt);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await SendDisconnectSignalAsync().ConfigureAwait(false);

        try
        {
            _cts?.Cancel();
        }
        catch { }

        for (var i = 0; i < PacketRouter.MaxRays; i++)
        {
            _txChannels[i]?.DrainAndReturn(b => ArrayPool<byte>.Shared.Return(b));
            _txChannels[i]?.Dispose();
            _txChannels[i] = null;
        }

        for (var i = 0; i < PacketRouter.MaxRays; i++)
        {
            if (_tunnelStreams[i] is { } stream)
            {
                try
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
                catch { }
                _tunnelStreams[i] = null;
            }
        }

        Dispose();
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch { }
        _cts = null;

        for (var i = 0; i < PacketRouter.MaxRays; i++)
        {
            _txChannels[i]?.DrainAndReturn(b => ArrayPool<byte>.Shared.Return(b));
            _txChannels[i]?.Dispose();
            _txChannels[i] = null;

            try
            {
                _tunnelStreams[i]?.Dispose();
                _tunnelStreams[i] = null;
            }
            catch { }

            try
            {
                if (_grpcChannels[i] is not null)
                {
                    _ = _grpcChannels[i]!.ShutdownAsync();
                    _grpcChannels[i]!.Dispose();
                    _grpcChannels[i] = null;
                }
            }
            catch { }
        }

        GC.SuppressFinalize(this);
    }
}

