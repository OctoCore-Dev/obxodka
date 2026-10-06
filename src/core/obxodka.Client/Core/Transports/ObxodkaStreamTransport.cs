using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using obxodka.Core.Transports;
using obxodka.Shared.Stealth;

namespace obxodka.Core.Transports;

public sealed class ObxodkaStreamTransport(int serverPort = 443, string? configuredSni = null) : IVpnTransport
{
    public string ProtocolName => "OBXODKA-STREAM";
    public bool IsConnected => Volatile.Read(ref _connected);

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
    private readonly TaskCompletionSource<(string ip, string ip6)> _ipTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

    private (HttpClient client, SocketsHttpHandler handler) CreateHttpClient(string targetHost, string serverIp)
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
                    ? (EndPoint)new IPEndPoint(ipAddr, _serverPort)
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

        (_httpClient0, _handler0) = CreateHttpClient(targetHost, serverIp);
        (_httpClient1, _handler1) = CreateHttpClient(targetHost, serverIp);

        _ = RunStreamAsync(0, _httpClient0, channelHost, thumbprint, linkedCts.Token);

        linkedCts.CancelAfter(TimeSpan.FromSeconds(15));
        using (linkedCts.Token.Register(() => _ipTcs.TrySetCanceled()))
        {
            var result = await _ipTcs.Task.ConfigureAwait(false);
            _activeRays = 2;
            _ = Task.Run(() => RunSecondaryRayAsync(channelHost, thumbprint, _cts.Token), _cts.Token);
            return result;
        }
    }

    private async Task RunSecondaryRayAsync(string channelHost, string thumbprint, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Volatile.Read(ref _connected))
        {
            try
            {
                await RunStreamAsync(1, _httpClient1!, channelHost, thumbprint, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Shared.Logging.AppLogger.LogWarning($"[OBXODKA-STREAM] Ray #1 dropped: {ex.Message}. Reconnecting in 2s...");
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

    private async Task RunStreamAsync(int ray, HttpClient client, string channelHost, string thumbprint, CancellationToken ct)
    {
        try
        {
            var requestUri = $"https://{channelHost}:{_serverPort}/api/v1/sync?ray={ray}";
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
                            var isPing = length == 10 && packet[0] == 0xAA && packet[1] == 0xBB;
                            var cmd = isPing ? ObxodkaFraming.CmdPing : ObxodkaFraming.CmdData;
                            var payloadSpan = isPing ? packet.AsSpan(2, 8) : packet.AsSpan(0, length);
                            var frame = ObxodkaFraming.Pack(cmd, payloadSpan, out var totalLen, maxMtu: 1360);
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

                if (!ObxodkaFraming.TryDecodeHeader(header, out var totalLen, out var payloadLen, out var command))
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
                                Volatile.Write(ref _connected, true);
                                _ = _ipTcs.TrySetResult((assignedIp, assignedIpV6));
                                Shared.Logging.AppLogger.Log($"[OBXODKA-ESTABLISHED] Tunnel online. IPv4={assignedIp}, IPv6={assignedIpV6}");
                                _ = Task.Run(() => PingLoopAsync(_cts!.Token), _cts!.Token);
                            }
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
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
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

        PacketRouter.GetRays(packet.AsSpan(0, length), _activeRays, out var primaryRay, out var secondaryRay);

        var ray0 = primaryRay % _activeRays;
        if (!_txChannels[ray0].Writer.TryWrite((packet, length)))
        {
            ArrayPool<byte>.Shared.Return(packet);
            return;
        }

        if (secondaryRay >= 0 && secondaryRay < _activeRays && secondaryRay != ray0)
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
        var raysCount = Math.Min(2, _activeRays);
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
