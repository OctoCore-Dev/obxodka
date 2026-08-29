namespace obxodka.Core.Mesh;

public sealed partial class MeshRelayServer(int speedMbps = 10) : IAsyncDisposable, IDisposable
{
    private const uint ObxmMagic = 0x4D58424F;
    public const int DefaultPort = 7443;
    private TcpListener? _tcpListener;
    private UdpClient? _udpListener;
    private UdpSessionTable? _udpSessions;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, int> _activeUserSessions = new();
    private readonly ConcurrentDictionary<IPAddress, (int count, DateTime window)> _udpRateLimit = new();
    private readonly Lock _stateLock = new();

    public MeshStats Stats { get; } = new();
    public BandwidthLimiter Limiter { get; } = new(speedMbps);
    public int BoundPort { get; private set; } = DefaultPort;
    public bool IsRunning { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        Start(cancellationToken);
        return Task.CompletedTask;
    }

    public void Start(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (IsRunning)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _udpSessions = new UdpSessionTable();

            BoundPort = BindListeners();
            IsRunning = true;
        }

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            _ = Task.Run(() =>
            {
                if (OperatingSystem.IsWindows())
                {
                    ManageFirewallRule(BoundPort, add: true);
                }
            }, CancellationToken.None);
        }
#endif

        _ = TcpAcceptLoopAsync(_cts.Token);
        _ = UdpRelayLoopAsync(_cts.Token);
        _ = HeartbeatLoopAsync(_cts.Token);
    }

    private int BindListeners()
    {
        int[] candidatePorts = [DefaultPort, 7444, 7445, 8443, 9443, 0];
        foreach (var port in candidatePorts)
        {
            try
            {
                var tcp = new TcpListener(IPAddress.Any, port);
                tcp.Start();
                var actualPort = ((IPEndPoint)tcp.LocalEndpoint).Port;

                var udp = new UdpClient(new IPEndPoint(IPAddress.Any, actualPort));

                _tcpListener = tcp;
                _udpListener = udp;
                return actualPort;
            }
            catch
            {
                _tcpListener?.Stop();
                _tcpListener = null;
                _udpListener?.Dispose();
                _udpListener = null;
            }
        }

        throw new InvalidOperationException("Не удалось открыть порты для Mesh Relay сервера.");
    }

    private static IPAddress GetPhysicalInterfaceIp()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in interfaces)
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var name = ni.Name.ToLowerInvariant();
                if (name.Contains("obxvpn", StringComparison.Ordinal) ||
                    name.Contains("obxodka", StringComparison.Ordinal) ||
                    name.Contains("wintun", StringComparison.Ordinal))
                {
                    continue;
                }

                var ipProps = ni.GetIPProperties();
                foreach (var unicast in ipProps.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(unicast.Address))
                    {
                        return unicast.Address;
                    }
                }
            }
        }
        catch { }

        return IPAddress.Any;
    }

    private async Task TcpAcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _tcpListener is not null)
        {
            try
            {
                var client = await _tcpListener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;
                client.ReceiveBufferSize = 8388608;
                client.SendBufferSize = 8388608;

                _ = Task.Run(() => HandleTcpClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RELAY TCP ACCEPT ERROR] {ex.Message}");
            }
        }
    }

    private static readonly byte[] t_handshakeOk = [0x01, 0x00, 0x01];
    private static readonly byte[] t_handshakeOverloaded = [0x00, 0x02, 0x01];

    private static (string Host, int Port) ParseHostAndPort(string targetStr)
    {
        if (string.IsNullOrWhiteSpace(targetStr))
        {
            return ("1.1.1.1", 443);
        }

        if (targetStr.StartsWith('[') && targetStr.Contains(']'))
        {
            var closeBracket = targetStr.IndexOf(']');
            var host = targetStr[1..closeBracket];
            var portPart = targetStr[(closeBracket + 1)..].TrimStart(':');
            var port = int.TryParse(portPart, out var p) ? p : 443;
            return (host, port);
        }

        var lastColon = targetStr.LastIndexOf(':');
        if (lastColon > 0)
        {
            var host = targetStr[..lastColon];
            var port = int.TryParse(targetStr[(lastColon + 1)..], out var p) ? p : 443;
            return (host, port);
        }

        return (targetStr, 443);
    }

    private async Task HandleTcpClientAsync(TcpClient client, CancellationToken ct)
    {
        using var clientScope = client;
        var userKey = string.Empty;
        var hasIncrementedSession = false;

        try
        {
            var stream = client.GetStream();

            var prefix = ArrayPool<byte>.Shared.Rent(5);
            try
            {
                await stream.ReadExactlyAsync(prefix.AsMemory(0, 5), ct).ConfigureAwait(false);
                var magic = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(0, 4));
                if (magic != ObxmMagic)
                {
                    return;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(prefix);
            }

            var lenBuf = ArrayPool<byte>.Shared.Rent(2);
            var jwtToken = string.Empty;
            var targetStr = string.Empty;
            try
            {
                await stream.ReadExactlyAsync(lenBuf.AsMemory(0, 2), ct).ConfigureAwait(false);
                var jwtLen = BinaryPrimitives.ReadUInt16LittleEndian(lenBuf);
                if (jwtLen > 0)
                {
                    var jwtBytes = ArrayPool<byte>.Shared.Rent(jwtLen);
                    try
                    {
                        await stream.ReadExactlyAsync(jwtBytes.AsMemory(0, jwtLen), ct).ConfigureAwait(false);
                        jwtToken = Encoding.UTF8.GetString(jwtBytes, 0, jwtLen);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(jwtBytes);
                    }
                }

                await stream.ReadExactlyAsync(lenBuf.AsMemory(0, 2), ct).ConfigureAwait(false);
                var targetLen = BinaryPrimitives.ReadUInt16LittleEndian(lenBuf);
                if (targetLen > 0)
                {
                    var targetBytes = ArrayPool<byte>.Shared.Rent(targetLen);
                    try
                    {
                        await stream.ReadExactlyAsync(targetBytes.AsMemory(0, targetLen), ct).ConfigureAwait(false);
                        targetStr = Encoding.UTF8.GetString(targetBytes, 0, targetLen);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(targetBytes);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(lenBuf);
            }

            var flagsBuf = ArrayPool<byte>.Shared.Rent(1);
            try
            {
                await stream.ReadExactlyAsync(flagsBuf.AsMemory(0, 1), ct).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(flagsBuf);
            }

            userKey = !string.IsNullOrWhiteSpace(jwtToken) ? jwtToken[..Math.Min(16, jwtToken.Length)] : client.Client.RemoteEndPoint?.ToString() ?? "anon";

            if (_activeUserSessions.Count >= MeshSettings.RelayMaxClients && !_activeUserSessions.ContainsKey(userKey))
            {
                await stream.WriteAsync(t_handshakeOverloaded.AsMemory(), ct).ConfigureAwait(false);
                return;
            }

            _ = _activeUserSessions.AddOrUpdate(userKey, 1, (_, count) => count + 1);
            hasIncrementedSession = true;
            Stats.IncrementClients();

            var (host, port) = ParseHostAndPort(targetStr);

            using var outbound = new Socket(SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
                SendBufferSize = 8388608,
                ReceiveBufferSize = 8388608
            };

            var physicalIp = GetPhysicalInterfaceIp();
            outbound.Bind(new IPEndPoint(physicalIp, 0));

            await outbound.ConnectAsync(host, port, ct).ConfigureAwait(false);
            using var outboundStream = new NetworkStream(outbound, ownsSocket: false);

            await stream.WriteAsync(t_handshakeOk.AsMemory(), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            using var proxyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var clientToTarget = ProxyStreamAsync(stream, outboundStream, proxyCts.Token);
            var targetToClient = ProxyStreamAsync(outboundStream, stream, proxyCts.Token);

            _ = await Task.WhenAny(clientToTarget, targetToClient).ConfigureAwait(false);
            try
            {
                proxyCts.Cancel();
            }
            catch { }
        }
        catch { }
        finally
        {
            if (hasIncrementedSession)
            {
                Stats.DecrementClients();
                _ = _activeUserSessions.AddOrUpdate(userKey, 0, (_, count) => Math.Max(0, count - 1));
                if (_activeUserSessions.TryGetValue(userKey, out var c) && c <= 0)
                {
                    _ = _activeUserSessions.TryRemove(userKey, out _);
                }
            }
        }
    }

    private async Task ProxyStreamAsync(Stream source, Stream destination, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (bytesRead <= 0)
                {
                    break;
                }

                await Limiter.ConsumeAsync(bytesRead, ct).ConfigureAwait(false);
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                Stats.AddBytes(bytesRead);
            }
        }
        catch { }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task UdpRelayLoopAsync(CancellationToken ct)
    {
        var physicalIp = GetPhysicalInterfaceIp();
        var apiHost = "obxodka.one";
        if (Uri.TryCreate(AppConfig.DefaultApiBaseUrl, UriKind.Absolute, out var uri))
        {
            apiHost = uri.Host;
        }

        while (!ct.IsCancellationRequested && _udpListener is not null && _udpSessions is not null)
        {
            try
            {
                var result = await _udpListener.ReceiveAsync(ct).ConfigureAwait(false);
                var clientEp = result.RemoteEndPoint;
                var data = result.Buffer;

                if (!IsUdpAllowed(clientEp.Address))
                {
                    continue;
                }

                _udpSessions.Touch(clientEp);

                var outbound = _udpSessions.GetOrCreate(clientEp, () =>
                {
                    var sock = new UdpClient(new IPEndPoint(physicalIp, 0));
                    sock.Connect(apiHost, 443);
                    _ = ForwardUdpServerToClientAsync(sock, clientEp, ct);
                    return sock;
                });

                await Limiter.ConsumeAsync(data.Length, ct).ConfigureAwait(false);
                _ = await outbound.SendAsync(data, data.Length).ConfigureAwait(false);
                Stats.AddBytes(data.Length);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch { }
        }
    }

    private async Task ForwardUdpServerToClientAsync(UdpClient outboundSock, IPEndPoint clientEp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udpListener is not null)
        {
            try
            {
                var res = await outboundSock.ReceiveAsync(ct).ConfigureAwait(false);
                await Limiter.ConsumeAsync(res.Buffer.Length, ct).ConfigureAwait(false);
                _ = await _udpListener.SendAsync(res.Buffer, res.Buffer.Length, clientEp).ConfigureAwait(false);
                Stats.AddBytes(res.Buffer.Length);
            }
            catch
            {
                break;
            }
        }
    }

    private bool IsUdpAllowed(IPAddress sourceIp)
    {
        var now = DateTime.UtcNow;
        var (count, window) = _udpRateLimit.AddOrUpdate(
            sourceIp,
            _ => (1, now),
            (_, old) => now - old.window > TimeSpan.FromSeconds(1) ? (1, now) : (old.count + 1, old.window)
        );

        if (_udpRateLimit.Count > 1000)
        {
            var cutoff = now.AddSeconds(-5);
            foreach (var (ip, data) in _udpRateLimit)
            {
                if (data.window < cutoff)
                {
                    _ = _udpRateLimit.TryRemove(ip, out _);
                }
            }
        }

        return count <= 500;
    }

    private static async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch { }
        }
    }

#if WINDOWS
    [SupportedOSPlatform("windows")]
    private static void ManageFirewallRule(int port, bool add)
    {
        try
        {
            var args = add
                ? $"advfirewall firewall add rule name=\"ObxodkaMeshRelay\" dir=in action=allow protocol=TCP localport={port}"
                : "advfirewall firewall delete rule name=\"ObxodkaMeshRelay\"";

            var psi = new ProcessStartInfo("netsh", args)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi)?.WaitForExit();
        }
        catch { }
    }
#endif

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_udpSessions is not null)
        {
            await _udpSessions.DisposeAsync().ConfigureAwait(false);
            _udpSessions = null;
        }
        GC.SuppressFinalize(this);
    }

    public Task StopAsync()
    {
        Stop();
        return Task.CompletedTask;
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            if (!IsRunning)
            {
                return;
            }

            IsRunning = false;
        }

        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch { }
        _cts = null;

        try
        {
            _tcpListener?.Server?.Dispose();
            _tcpListener?.Dispose();
        }
        catch { }
        _tcpListener = null;

        try
        {
            _udpListener?.Dispose();
        }
        catch { }
        _udpListener = null;

        _udpSessions?.Dispose();
        _udpSessions = null;

        Stats.Reset();

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            _ = Task.Run(() =>
            {
                if (OperatingSystem.IsWindows())
                {
                    ManageFirewallRule(BoundPort, add: false);
                }
            }, CancellationToken.None);
        }
#endif
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}

