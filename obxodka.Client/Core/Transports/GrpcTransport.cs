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
    private readonly X509Certificate2? _clientCert = clientCert;
    private readonly string? _jwtToken = jwtToken;
    private readonly int _serverPort = serverPort > 0 ? serverPort : 443;
    private readonly MeshRelayInfo? _meshRelay = meshRelay;
    private readonly string? _configuredSni = targetSni;
    private string _thumbprint = string.Empty;

    private readonly GrpcChannel?[] _grpcChannels = new GrpcChannel?[PacketRouter.MaxRays];
    private readonly Channel<(byte[] buffer, int length)>?[] _txChannels = new Channel<(byte[], int)>?[PacketRouter.MaxRays];
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

                Debug.WriteLine($"[CERT PINNING MISMATCH] Expected: {expectedPin}, Actual: {hash}");
                return false;
            }

            var nowUtc = DateTime.UtcNow;
            var notBeforeUtc = cert2.NotBefore.ToUniversalTime();
            var notAfterUtc = cert2.NotAfter.ToUniversalTime();

            if (nowUtc < notBeforeUtc - TimeSpan.FromDays(1) || nowUtc > notAfterUtc)
            {
                return false;
            }

            if (errors != SslPolicyErrors.None)
            {
                return false;
            }

            if (chain is not null)
            {
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                if (!chain.Build(cert2))
                {
                    return false;
                }
            }

            return true;
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
        var targetHost = !string.IsNullOrWhiteSpace(_configuredSni)
            ? _configuredSni
            : serverIp;

        try
        {
            for (var i = 0; i < _activeRays; i++)
            {
                var handler = new SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true,
                    PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                    KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                    KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                    KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        TargetHost = targetHost,
                        EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
                        ClientCertificates = _clientCert != null ? [_clientCert] : null,
                        RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
                            ValidateServerCertificate(certificate, chain, errors)
                    },
                    InitialHttp2StreamWindowSize = 16777216
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
                        await socket.ConnectAsync(context.DnsEndPoint, cToken);
                        return new NetworkStream(socket, ownsSocket: true);
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

                _grpcChannels[i] = GrpcChannel.ForAddress($"https://{serverIp}:{serverPort}", channelOptions);
                _txChannels[i] = Channel.CreateBounded<(byte[], int)>(new BoundedChannelOptions(i == 0 ? 2000 : 1500) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
                await ConnectRayAsync(i, isNewConnection: i == 0);
                _ = TxLoopAsync(i, _txChannels[i]!, _cts.Token);
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

    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var packet = ArrayPool<byte>.Shared.Rent(9);
                packet[0] = 0x99;
                BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(1, 8), DateTime.UtcNow.Ticks);
                SendPacketFromPool(packet, 9);
            }
            catch { }
            await Task.Delay(2000, ct);
        }
    }

    private async Task ConnectRayAsync(int rayIndex, bool isNewConnection)
    {
        var client = new TunnelService.TunnelServiceClient(_grpcChannels[rayIndex]);
        var headers = new Metadata();

        var authThumb = !string.IsNullOrEmpty(_thumbprint) ? _thumbprint : _clientCert?.Thumbprint ?? "";
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

        var call = client.ConnectStream(headers, cancellationToken: _cts!.Token);
        var handshake = new byte[16];
        handshake[0] = 16;
        handshake[1] = (byte)rayIndex;
        handshake[2] = (byte)(isNewConnection ? 1 : 0);

        await call.RequestStream.WriteAsync(new TunnelPacket { Data = ByteString.CopyFrom(handshake) });
        _tunnelStreams[rayIndex] = new TunnelGrpcStream(call);
        _ = ReceiveLoopAsync(rayIndex, _cts.Token);
    }

    private async Task TxLoopAsync(int rayIndex, Channel<(byte[] buffer, int length)> txChannel, CancellationToken ct)
    {
        var isGamingRay = rayIndex == 0 || rayIndex == (_activeRays - 1);
        var batchBuffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (buffer, length) = await txChannel.Reader.ReadAsync(ct).ConfigureAwait(false);
                var offset = 0;
                Buffer.BlockCopy(buffer, 0, batchBuffer, offset, length);
                offset += length;
                ArrayPool<byte>.Shared.Return(buffer);

                if (!isGamingRay)
                {
                    while (offset < 32768 && txChannel.Reader.TryRead(out var nextPkt))
                    {
                        if (offset + nextPkt.length <= batchBuffer.Length)
                        {
                            Buffer.BlockCopy(nextPkt.buffer, 0, batchBuffer, offset, nextPkt.length);
                            offset += nextPkt.length;
                        }
                        ArrayPool<byte>.Shared.Return(nextPkt.buffer);
                    }
                }

                if (_tunnelStreams[rayIndex] is { } stream)
                {
                    await stream.WriteAsync(batchBuffer.AsMemory(0, offset), ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GRPC TX FAIL #{rayIndex}] {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(batchBuffer);
        }
    }

    private async Task ReceiveLoopAsync(int rayIndex, CancellationToken ct)
    {
        var header = new byte[8];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_tunnelStreams[rayIndex] is not { } stream)
                {
                    break;
                }

                var (packet, realLen) = await Obfuscator.ReadPacketAsync(stream, header, ct).ConfigureAwait(false);
                if (packet is null)
                {
                    break;
                }

                if (realLen > 0)
                {
                    if (realLen >= 3 && packet[0] == 'I' && packet[1] == 'P' && packet[2] == ':')
                    {
                        var msg = Encoding.UTF8.GetString(packet, 0, realLen);
                        ArrayPool<byte>.Shared.Return(packet);
                        var parts = msg.Split('|');
                        var ip = parts[0].Replace("IP:", "", StringComparison.Ordinal);
                        var ip6 = parts.Length > 1 ? parts[1].Replace("IP6:", "", StringComparison.Ordinal) : "fd00::2";
                        _ = (_ipTcs?.TrySetResult((ip, ip6)));
                    }
                    else if (packet[0] == 0x99 && realLen == 9)
                    {
                        var sentTicks = BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(1, 8));
                        var rtt = (DateTime.UtcNow.Ticks - sentTicks) / TimeSpan.TicksPerMillisecond;
                        OnPingUpdated?.Invoke(Math.Max(1, rtt));
                        ArrayPool<byte>.Shared.Return(packet);
                    }
                    else
                    {
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
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GRPC RX FAIL #{rayIndex}] {ex.Message}");
            OnConnectionDropped?.Invoke();
        }
    }

    public void SendPacketFromPool(byte[] packet, int length)
    {
        if (!IsConnected)
        {
            ArrayPool<byte>.Shared.Return(packet);
            return;
        }

        PacketRouter.GetRays(packet, length, _activeRays, out var primaryRay, out var secondaryRay);
        var primaryChannel = _txChannels[primaryRay];
        if (primaryChannel is null)
        {
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

        if (!primaryChannel.Writer.TryWrite((packed, totalLength)))
        {
            ArrayPool<byte>.Shared.Return(packed);
        }

        if (dup is not null)
        {
            var secondaryChannel = _txChannels[secondaryRay];
            if (secondaryChannel is null || !secondaryChannel.Writer.TryWrite((dup, totalLength)))
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
            _ = (_txChannels[i]?.Writer.TryComplete());
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
        _clientCert?.Dispose();

        for (var i = 0; i < PacketRouter.MaxRays; i++)
        {
            _ = (_txChannels[i]?.Writer.TryComplete());
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
