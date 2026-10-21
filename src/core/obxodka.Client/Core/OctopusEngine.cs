namespace obxodka.Core;

public sealed partial class OctopusEngine : IDisposable, IAsyncDisposable
{
    private static readonly Lazy<OctopusEngine> t_instance = new(
        () => new OctopusEngine(),
        LazyThreadSafetyMode.ExecutionAndPublication
    );
    public static OctopusEngine Current => t_instance.Value;

    [SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance")]
    private IVpnTransport? _transport;
    private X509Certificate2? _clientCert;
    private CancellationTokenSource? _cts;
    private Action<Socket>? _protectAction;
    public static string? DynamicSslPublicKeyHash { get; set; }

    public int ActiveRays { get; private set; } = 1;

    public bool IsConnected => _transport is { IsConnected: true };
    public string AssignedIp { get; private set; } = "10.8.0.2";
    public string AssignedIpV6 { get; private set; } = "fd00::2";
    public string? PublicWanIp { get; set; }

    public event Action<byte[], int>? OnPacketReceived;
    private event Action<byte[], int>? OnProbePacketInspect;
    public event Action? OnConnectionDropped;
    public event Action? OnDeadConnectionDetected;
    public event Action<long, long>? OnTrafficUpdated;
    public event Action<long>? OnPingUpdated;
    public event Action<string>? OnStatusMessage;
    public event Action<int>? OnMtuUpdated;
    public static event Action<string>? OnCertificateRevoked;

    public void EmitStatus(string message)
    {
        OnStatusMessage?.Invoke(message);
        Shared.Logging.AppLogger.Log(message);
    }

    private long _totalBytesSent;
    private long _totalBytesReceived;
    public long TotalBytesSent => Interlocked.Read(ref _totalBytesSent);
    public long TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);

    public string ActiveProtocol { get; private set; } = "AUTO";
    public string ActiveSni => (_transport as ObxodkaStreamTransport)?.ActiveSni ?? AppSecrets.AllowedSniPool[0];

    private long _watchdogArmedTicks;
    private CancellationTokenSource? _mtuScanCts;
    private int _isScanningMtu;
    private long _largeTxPackets;
    private long _largeRxPackets;

    public void ArmTrafficWatchdog()
    {
        Volatile.Write(ref _watchdogArmedTicks, Environment.TickCount64);
        ScheduleSmartMtuScan(2500);
    }

    public void ScheduleSmartMtuScan(int delayMs = 2500)
    {
        try
        {
            _mtuScanCts?.Cancel();
            _mtuScanCts?.Dispose();
        }
        catch { }

        var engineCts = _cts;
        if (engineCts is null || engineCts.IsCancellationRequested)
        {
            return;
        }

        var scanCts = CancellationTokenSource.CreateLinkedTokenSource(engineCts.Token);
        _mtuScanCts = scanCts;
        var token = scanCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                }

                if (!token.IsCancellationRequested && IsConnected)
                {
                    _ = await ScanAndLockOptimalMtuAsync(token).ConfigureAwait(false);
                }
            }
            catch { }
        }, token);
    }

    public void ResetTrafficCounters()
    {
        _ = Interlocked.Exchange(ref _totalBytesSent, 0);
        _ = Interlocked.Exchange(ref _totalBytesReceived, 0);
        _ = Interlocked.Exchange(ref _largeTxPackets, 0);
        _ = Interlocked.Exchange(ref _largeRxPackets, 0);
        Volatile.Write(ref _watchdogArmedTicks, 0);
        _smoothedPing = 0;
    }

    private double _smoothedPing;

    private void ReportPing(long rawRtt)
    {
        if (rawRtt <= 0)
        {
            return;
        }
        _ = Interlocked.Add(ref _totalBytesReceived, 9);
        if (_smoothedPing <= 0)
        {
            _smoothedPing = rawRtt;
        }
        else if (rawRtt < _smoothedPing)
        {
            var alpha = 0.55;
            _smoothedPing = (_smoothedPing * (1 - alpha)) + (rawRtt * alpha);
        }
        else
        {
            var alpha = rawRtt > _smoothedPing * 1.5 ? 0.05 : 0.20;
            _smoothedPing = (_smoothedPing * (1 - alpha)) + (rawRtt * alpha);
        }
        OnPingUpdated?.Invoke((long)Math.Round(_smoothedPing));
    }

    private volatile uint[] _serverIpv4Array = [];
    private volatile byte[][] _serverIpv6Array = [];
    private readonly Lock _serverIpsLock = new();

    public void RegisterServerEndpoint(string serverIp)
    {
        if (string.IsNullOrWhiteSpace(serverIp))
        {
            return;
        }

        lock (_serverIpsLock)
        {
            try
            {
                var v4List = _serverIpv4Array.ToList();
                var v6List = _serverIpv6Array.ToList();
                var changed = false;

                void Add(IPAddress ip)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var u = BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
                        if (!v4List.Contains(u))
                        {
                            v4List.Add(u);
                            changed = true;
                        }
                    }
                    else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        var b = ip.GetAddressBytes();
                        if (!v6List.Any(existing => existing.AsSpan().SequenceEqual(b)))
                        {
                            v6List.Add(b);
                            changed = true;
                        }
                    }
                }

                if (IPAddress.TryParse(serverIp, out var directIp))
                {
                    Add(directIp);
                }
                else
                {
                    var addrs = Dns.GetHostAddresses(serverIp);
                    foreach (var addr in addrs)
                    {
                        Add(addr);
                    }
                }

                if (changed)
                {
                    _serverIpv4Array = [.. v4List];
                    _serverIpv6Array = [.. v6List];
                }
            }
            catch { }
        }
    }

    public bool IsServerDestination(ReadOnlySpan<byte> packet, int length)
    {
        if (length < 20)
        {
            return false;
        }

        var version = packet[0] >> 4;
        if (version == 4)
        {
            var dst = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(16, 4));
            var v4 = _serverIpv4Array;
            for (var i = 0; i < v4.Length; i++)
            {
                if (v4[i] == dst)
                {
                    return true;
                }
            }
        }
        else if (version == 6 && length >= 40)
        {
            var dstSpan = packet.Slice(24, 16);
            var v6 = _serverIpv6Array;
            for (var i = 0; i < v6.Length; i++)
            {
                if (dstSpan.SequenceEqual(v6[i]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public async Task ConnectAsync(string serverIp, int serverPort, string? protocolOverride = null, string? targetSni = null)
    {
        _ = protocolOverride;
        if (IsConnected)
        {
            return;
        }

        NetworkDefaults.CurrentMtu = NetworkDefaults.DefaultMtu;
        RegisterServerEndpoint(serverIp);
        RegisterServerEndpoint(AppConfig.DirectServerIp);

        var session = await AuthManager.LoadSessionAsync();
        if (string.IsNullOrEmpty(session.VpnConfig))
        {
            throw new InvalidOperationException("Сертификат VPN отсутствует. Авторизуйтесь заново.");
        }

        var certBytes = Convert.FromBase64String(session.VpnConfig);
        try
        {
            _clientCert = X509CertificateLoader.LoadPkcs12(certBytes, AppSecrets.InternalPfxPassword, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        }
        catch
        {
            Debug.WriteLine("[VPN] Обнаружен устаревший сертификат. Требуется повторная авторизация.");
            OnCertificateRevoked?.Invoke("Обнаружен устаревший сертификат. Пожалуйста, войдите снова.");
            throw new UnauthorizedAccessException("Old certificate");
        }

        _cts = new CancellationTokenSource();

        var configuredRays = Preferences.Get("BatteryMode", 1);
        _ = configuredRays;
        ActiveRays = 2;

        var effectiveSni = !string.IsNullOrWhiteSpace(targetSni)
            ? targetSni
            : (IPAddress.TryParse(serverIp, out _) ? AppSecrets.GetRandomSni() : null);

        Shared.Logging.AppLogger.Log($"[OCTOPUS] Initializing transport to {serverIp}:{serverPort} with SNI '{effectiveSni ?? "none"}'");
        var transport = new ObxodkaStreamTransport(serverPort, effectiveSni);
        if (_protectAction is not null)
        {
            transport.ProtectSockets(_protectAction);
        }
        _transport = transport;
        ActiveProtocol = transport.ProtocolName;
        transport.OnPacketReceived += (pkt, len) =>
        {
            _ = Interlocked.Add(ref _totalBytesReceived, len);
            if (len > 600)
            {
                _ = Interlocked.Increment(ref _largeRxPackets);
            }
            OnProbePacketInspect?.Invoke(pkt, len);
            var handler = OnPacketReceived;
            if (handler is not null)
            {
                handler.Invoke(pkt, len);
            }
            else
            {
                ArrayPool<byte>.Shared.Return(pkt);
            }
        };
        transport.OnPingUpdated += ReportPing;
        transport.OnConnectionDropped += () => OnConnectionDropped?.Invoke();

        var (ip, ip6) = await transport.ConnectAsync(serverIp, _clientCert?.Thumbprint ?? "", _cts.Token);
        AssignedIp = ip;
        AssignedIpV6 = ip6;
        Shared.Logging.AppLogger.Log($"[OCTOPUS] Handshake complete with {serverIp}:{serverPort}! Assigned IPv4: {ip}, IPv6: {ip6}");
        StartTrafficMonitor();
    }

    public async Task ReconnectAsync(string serverIp, int serverPort, string? protocolOverride = null, string? targetSni = null)
    {
        await DisposeAsync();
        await ConnectAsync(serverIp, serverPort, protocolOverride, targetSni);
    }

    public async Task<bool> VerifyDownlinkAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (!IsConnected || _transport is null)
        {
            Shared.Logging.AppLogger.LogWarning("[PROBE FAIL] Engine is not connected or transport is null.");
            return false;
        }

        var initialReceived = TotalBytesReceived;
        var initialSent = TotalBytesSent;

        if (initialReceived > 0)
        {
            Shared.Logging.AppLogger.Log($"[PROBE SUCCESS] Downlink already active with {initialReceived}B received.");
            _ = _transport.SendMtuSyncAsync(NetworkDefaults.CurrentMtu);
            return true;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts?.Token ?? CancellationToken.None);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnPacket(byte[] pkt, int len)
        {
            if (len > 0)
            {
                var proto = len >= 20 ? ((pkt[0] >> 4) == 4 ? $"IPv4(proto={pkt[9]})" : "IPv6") : "NonIP";
                Shared.Logging.AppLogger.Log($"[PROBE RX] Received verified downlink packet ({proto}, {len}B). Total RX={TotalBytesReceived}B");
                _ = tcs.TrySetResult(true);
            }
        }

        void OnPing(long rtt)
        {
            EmitStatus(VpnStatusMessages.PingReceived(rtt));
            _ = tcs.TrySetResult(true);
        }

        OnProbePacketInspect += OnPacket;
        OnPingUpdated += OnPing;

        var probeCycle = 0;
        try
        {
            async Task SendProbesAsync()
            {
                var cycle = Interlocked.Increment(ref probeCycle);
                try
                {
                    await (_transport?.SendPingProbeAsync() ?? Task.CompletedTask).ConfigureAwait(false);
                }
                catch { }

                try
                {
                    if (BuildIcmpProbePacket(AssignedIp, "100.64.0.1", 28) is { } pIcmpSelf)
                    {
                        _ = SendPacketAsync(pIcmpSelf);
                    }
                    if (cycle % 2 == 1 && BuildDnsProbePacket(AssignedIp, 1) is { } p1)
                    {
                        _ = SendPacketAsync(p1);
                    }
                }
                catch { }

                Shared.Logging.AppLogger.Log($"[PROBE TX #{cycle}] Sent lightweight Ping + ICMP(28B). TotalSent={TotalBytesSent}B, TotalRecv={TotalBytesReceived}B");
            }

            _ = Task.Run(SendProbesAsync, linkedCts.Token);

            var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < deadline && !linkedCts.IsCancellationRequested)
            {
                if (TotalBytesReceived > initialReceived)
                {
                    var curTx = TotalBytesSent - initialSent;
                    var curRx = TotalBytesReceived - initialReceived;
                    _ = _transport?.SendMtuSyncAsync(NetworkDefaults.CurrentMtu);
                    EmitStatus(VpnStatusMessages.MtuLocked(NetworkDefaults.CurrentMtu));
                    EmitStatus(VpnStatusMessages.ChannelVerifiedDuplex(curTx, curRx));
                    return true;
                }

                var delayTask = Task.Delay(400, linkedCts.Token);
                var completed = await Task.WhenAny(tcs.Task, delayTask).ConfigureAwait(false);
                if (completed == tcs.Task && await tcs.Task.ConfigureAwait(false))
                {
                    var curTx = TotalBytesSent - initialSent;
                    var curRx = TotalBytesReceived - initialReceived;
                    _ = _transport?.SendMtuSyncAsync(NetworkDefaults.CurrentMtu);
                    EmitStatus(VpnStatusMessages.MtuLocked(NetworkDefaults.CurrentMtu));
                    EmitStatus(VpnStatusMessages.ChannelVerifiedDuplex(curTx, curRx));
                    return true;
                }

                _ = Task.Run(SendProbesAsync, linkedCts.Token);
            }

            var deltaRx = TotalBytesReceived - initialReceived;
            var deltaTx = TotalBytesSent - initialSent;
            if (deltaTx <= 0)
            {
                EmitStatus(VpnStatusMessages.TxTransmissionFailed);
                Shared.Logging.AppLogger.LogError("[PROBE TIMEOUT] Outgoing transmission failed (TX=0). Sockets or local firewall blocked packets.");
            }
            else if (deltaRx <= 0)
            {
                EmitStatus(VpnStatusMessages.RxResponseTimeout(deltaTx));
                Shared.Logging.AppLogger.LogError($"[PROBE TIMEOUT] Server downlink verification failed after {timeout.TotalSeconds:F1}s! Sent {deltaTx}B across {probeCycle} probe cycles, but received 0 bytes from server.");
            }
            else
            {
                _ = _transport?.SendMtuSyncAsync(NetworkDefaults.CurrentMtu);
                EmitStatus(VpnStatusMessages.MtuLocked(NetworkDefaults.CurrentMtu));
            }
            return deltaRx > 0 && deltaTx > 0;
        }
        catch (OperationCanceledException)
        {
            var deltaRx = TotalBytesReceived - initialReceived;
            var deltaTx = TotalBytesSent - initialSent;
            if (deltaRx > 0)
            {
                _ = _transport?.SendMtuSyncAsync(NetworkDefaults.CurrentMtu);
                EmitStatus(VpnStatusMessages.MtuLocked(NetworkDefaults.CurrentMtu));
            }
            return deltaRx > 0 && deltaTx > 0;
        }
        finally
        {
            OnProbePacketInspect -= OnPacket;
            OnPingUpdated -= OnPing;
        }
    }

    public async Task<int> ScanAndLockOptimalMtuAsync(CancellationToken ct = default)
    {
        if (!IsConnected || _transport is null)
        {
            return NetworkDefaults.CurrentMtu;
        }

        if (Interlocked.CompareExchange(ref _isScanningMtu, 1, 0) != 0)
        {
            return NetworkDefaults.CurrentMtu;
        }

        try
        {
            EmitStatus(VpnStatusMessages.ScanningMtuHole(NetworkDefaults.MaxMtu, NetworkDefaults.MinMtu, 8));
            var burstResults = await _transport.MeasureBurstMtuAsync(ct).ConfigureAwait(false);
            int optimalMtu;
            if (burstResults.Length > 0)
            {
                var maxWireMtu = burstResults.Max();
                var candidateMtu = maxWireMtu >= 1500 ? 1400 : Math.Clamp(maxWireMtu - 80, NetworkDefaults.MinMtu, NetworkDefaults.MaxMtu);
                var uplinkOk = await (_transport?.ProbeMtuAsync(candidateMtu, ct) ?? Task.FromResult(false)).ConfigureAwait(false);
                if (uplinkOk)
                {
                    optimalMtu = candidateMtu;
                    EmitStatus(VpnStatusMessages.MtuHoleFound(optimalMtu));
                }
                else
                {
                    optimalMtu = await EscalateMtuAsync(NetworkDefaults.DefaultMtu, ct).ConfigureAwait(false);
                }
            }
            else
            {
                optimalMtu = await EscalateMtuAsync(NetworkDefaults.CurrentMtu, ct).ConfigureAwait(false);
            }

            NetworkDefaults.CurrentMtu = optimalMtu;
            EmitStatus(VpnStatusMessages.MtuSyncingWithServer(NetworkDefaults.CurrentMtu));
            await (_transport?.SendMtuSyncAsync(optimalMtu) ?? Task.CompletedTask).ConfigureAwait(false);
            OnMtuUpdated?.Invoke(optimalMtu);
            EmitStatus(VpnStatusMessages.MtuLocked(NetworkDefaults.CurrentMtu));
            return NetworkDefaults.CurrentMtu;
        }
        finally
        {
            Volatile.Write(ref _isScanningMtu, 0);
        }
    }

    private async Task<int> EscalateMtuAsync(int initialMtu, CancellationToken ct)
    {
        var bestMtu = Math.Max(initialMtu, NetworkDefaults.MinMtu);
        var steps = NetworkDefaults.MtuEscalationSteps.Where(s => s > bestMtu).OrderBy(s => s);
        foreach (var candidate in steps)
        {
            if (ct.IsCancellationRequested || !IsConnected)
            {
                break;
            }

            var transportOk = await (_transport?.ProbeMtuAsync(candidate, ct) ?? Task.FromResult(false)).ConfigureAwait(false);
            if (!transportOk)
            {
                break;
            }

            if (BuildIcmpProbePacket(AssignedIp, "100.64.0.1", candidate) is { } probe)
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnStepPacket(byte[] pkt, int len)
                {
                    if (len == candidate && IsIcmpEchoReply(pkt, len))
                    {
                        _ = tcs.TrySetResult(true);
                    }
                }

                OnProbePacketInspect += OnStepPacket;
                try
                {
                    _ = SendPacketAsync(probe);
                    var timeoutMs = Math.Clamp((int)(_smoothedPing > 0 ? _smoothedPing * 2.5 : 750), 350, 1500);
                    var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs, ct)).ConfigureAwait(false);
                    if (completed != tcs.Task || !await tcs.Task.ConfigureAwait(false))
                    {
                        break;
                    }
                }
                finally
                {
                    OnProbePacketInspect -= OnStepPacket;
                }
            }

            bestMtu = candidate;
            EmitStatus(VpnStatusMessages.MtuHoleFound(bestMtu));
        }

        return bestMtu;
    }

    private static bool IsIcmpEchoReply(byte[] pkt, int len)
    {
        if (len >= 28 && (pkt[0] >> 4) == 4 && pkt[9] == 1 && (pkt[0] & 0x0F) >= 5)
        {
            var ihl = (pkt[0] & 0x0F) * 4;
            return len >= ihl + 8 && pkt[ihl] == 0;
        }
        return false;
    }

    private static byte[]? BuildIcmpProbePacket(string assignedIp, string targetIp = "100.64.0.1", int totalLen = 28)
    {
        if (totalLen < 28)
        {
            totalLen = 28;
        }

        if (!IPAddress.TryParse(assignedIp, out var srcIp) || srcIp.AddressFamily != AddressFamily.InterNetwork ||
            !IPAddress.TryParse(targetIp, out var dstIp) || dstIp.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        var packet = new byte[totalLen];

        packet[0] = 0x45;
        packet[1] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), (ushort)totalLen);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), 0x7788);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), 0x4000);
        packet[8] = 64;
        packet[9] = 1;
        srcIp.GetAddressBytes().CopyTo(packet.AsSpan(12, 4));
        dstIp.GetAddressBytes().CopyTo(packet.AsSpan(16, 4));

        var ipChecksum = ComputeChecksum(packet.AsSpan(0, 20));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10, 2), ipChecksum);

        packet[20] = 8;
        packet[21] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(24, 2), 0x1337);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(26, 2), 1);

        for (var i = 28; i < totalLen; i++)
        {
            packet[i] = (byte)(i & 0xFF);
        }

        var icmpChecksum = ComputeChecksum(packet.AsSpan(20, totalLen - 20));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), icmpChecksum);

        return packet;
    }

    private static byte[]? BuildDnsProbePacket(string assignedIp, byte dstOctet = 1)
    {
        if (!IPAddress.TryParse(assignedIp, out var srcIp) || srcIp.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        byte[] dns = [
            0x1A, 0x2B, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x03, 0x64, 0x6e, 0x73, 0x06, 0x67, 0x6f, 0x6f, 0x67, 0x6c, 0x65, 0x00,
            0x00, 0x01, 0x00, 0x01
        ];

        var udpLen = 8 + dns.Length;
        var totalLen = 20 + udpLen;
        var packet = new byte[totalLen];

        packet[0] = 0x45;
        packet[1] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), (ushort)totalLen);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), 0x5432);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), 0x4000);
        packet[8] = 64;
        packet[9] = 17;
        srcIp.GetAddressBytes().CopyTo(packet.AsSpan(12, 4));
        packet[16] = dstOctet == 8 ? (byte)8 : (byte)1;
        packet[17] = dstOctet == 8 ? (byte)8 : (byte)1;
        packet[18] = dstOctet == 8 ? (byte)8 : (byte)1;
        packet[19] = dstOctet == 8 ? (byte)8 : (byte)1;

        var ipChecksum = ComputeChecksum(packet.AsSpan(0, 20));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10, 2), ipChecksum);

        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20, 2), 53535);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), 53);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(24, 2), (ushort)udpLen);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(26, 2), 0);

        dns.CopyTo(packet.AsSpan(28));
        return packet;
    }

    private static ushort ComputeChecksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i += 2)
        {
            if (i + 1 < data.Length)
            {
                sum += (uint)((data[i] << 8) + data[i + 1]);
            }
            else
            {
                sum += (uint)(data[i] << 8);
            }
        }
        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        return (ushort)~sum;
    }

    private void StartTrafficMonitor()
    {
        var token = _cts?.Token ?? CancellationToken.None;
        if (token == CancellationToken.None)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            long lastSent = 0;
            long lastReceived = 0;
            long windowLargeTx = 0;
            long windowLargeRx = 0;
            var windowTicks = 0;
            var mtuBlackholeWindows = 0;
            var deadTicks = 0;

            void OnPing(long _) => deadTicks = 0;
            OnPingUpdated += OnPing;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var currentSent = TotalBytesSent;
                    var currentReceived = TotalBytesReceived;
                    OnTrafficUpdated?.Invoke(currentSent, currentReceived);

                    var armedTicks = Volatile.Read(ref _watchdogArmedTicks);
                    if (armedTicks == 0)
                    {
                        deadTicks = 0;
                        windowTicks = 0;
                        mtuBlackholeWindows = 0;
                        lastSent = currentSent;
                        lastReceived = currentReceived;
                        windowLargeTx = Volatile.Read(ref _largeTxPackets);
                        windowLargeRx = Volatile.Read(ref _largeRxPackets);
                        await Task.Delay(200, token);
                        continue;
                    }

                    if (currentSent > lastSent && currentReceived == lastReceived)
                    {
                        deadTicks++;
                        var elapsedMs = Environment.TickCount64 - armedTicks;
                        var isInitialBlackhole = elapsedMs is >= 15000 and < 60000 && currentSent > 25000 && currentReceived == 0;

                        var isDead = (isInitialBlackhole && deadTicks >= 50) ||
                                     (currentSent > 50000 && currentReceived == 0 && deadTicks >= 60) ||
                                     deadTicks >= 90;

                        if (isDead)
                        {
                            Debug.WriteLine($"[ENGINE] Dead connection detected. InitialBlackhole={isInitialBlackhole}, TX={currentSent}, RX={currentReceived}, DeadTicks={deadTicks}");
                            OnDeadConnectionDetected?.Invoke();
                            break;
                        }
                    }
                    else if (currentReceived > lastReceived)
                    {
                        deadTicks = 0;
                    }

                    windowTicks++;
                    if (windowTicks >= 25)
                    {
                        windowTicks = 0;
                        var curLargeTx = Volatile.Read(ref _largeTxPackets);
                        var curLargeRx = Volatile.Read(ref _largeRxPackets);
                        var deltaLargeTx = curLargeTx - windowLargeTx;
                        var deltaLargeRx = curLargeRx - windowLargeRx;
                        windowLargeTx = curLargeTx;
                        windowLargeRx = curLargeRx;

                        if (NetworkDefaults.CurrentMtu > NetworkDefaults.DefaultMtu && Volatile.Read(ref _isScanningMtu) == 0)
                        {
                            if (deltaLargeTx >= 10 && deltaLargeRx == 0)
                            {
                                mtuBlackholeWindows++;
                                if (mtuBlackholeWindows >= 2)
                                {
                                    mtuBlackholeWindows = 0;
                                    NetworkDefaults.CurrentMtu = NetworkDefaults.DefaultMtu;
                                    _ = _transport?.SendMtuSyncAsync(NetworkDefaults.DefaultMtu);
                                    OnMtuUpdated?.Invoke(NetworkDefaults.DefaultMtu);
                                    Shared.Logging.AppLogger.LogWarning($"[SMART MTU] Mid-session MTU blackhole detected (LargeTX={deltaLargeTx}, LargeRX=0). Stepped down to {NetworkDefaults.DefaultMtu}B and rescheduling scan.");
                                    ScheduleSmartMtuScan(2000);
                                }
                            }
                            else if (deltaLargeRx > 0)
                            {
                                mtuBlackholeWindows = 0;
                            }
                        }
                        else
                        {
                            mtuBlackholeWindows = 0;
                        }
                    }

                    lastSent = currentSent;
                    lastReceived = currentReceived;
                    await Task.Delay(200, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                OnPingUpdated -= OnPing;
            }
        }, token);
    }

    public Task SendPacketAsync(byte[] packet)
    {
        if (!IsConnected || _transport is null || packet.Length == 0 || IsServerDestination(packet.AsSpan(0, packet.Length), packet.Length))
        {
            return Task.CompletedTask;
        }

        var poolBuf = ArrayPool<byte>.Shared.Rent(packet.Length);
        Buffer.BlockCopy(packet, 0, poolBuf, 0, packet.Length);
        _ = Interlocked.Add(ref _totalBytesSent, packet.Length);
        if (packet.Length > NetworkDefaults.DefaultMtu)
        {
            _ = Interlocked.Increment(ref _largeTxPackets);
        }
        _transport.SendPacketFromPool(poolBuf, packet.Length);
        return Task.CompletedTask;
    }

    public void SendPacketFromPool(byte[] inputBuf, int length)
    {
        if (!IsConnected || _transport is null || IsServerDestination(inputBuf.AsSpan(0, length), length))
        {
            ArrayPool<byte>.Shared.Return(inputBuf);
            return;
        }

        _ = Interlocked.Add(ref _totalBytesSent, length);
        if (length > NetworkDefaults.DefaultMtu)
        {
            _ = Interlocked.Increment(ref _largeTxPackets);
        }
        _transport.SendPacketFromPool(inputBuf, length);
    }

    public Task SendPacketFromPoolAsync(byte[] inputBuf, int length)
    {
        SendPacketFromPool(inputBuf, length);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        Volatile.Write(ref _watchdogArmedTicks, 0);
        _smoothedPing = 0;
        try
        {
            _mtuScanCts?.Cancel();
            _mtuScanCts?.Dispose();
        }
        catch { }
        _mtuScanCts = null;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _clientCert?.Dispose();
        _clientCert = null;

        if (_transport is not null)
        {
            await _transport.DisposeAsync();
            _transport = null;
        }

        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        Volatile.Write(ref _watchdogArmedTicks, 0);
        try
        {
            _mtuScanCts?.Cancel();
            _mtuScanCts?.Dispose();
        }
        catch { }
        _mtuScanCts = null;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _clientCert?.Dispose();
        _clientCert = null;

        _transport?.Dispose();
        _transport = null;

        GC.SuppressFinalize(this);
    }

    public void ProtectTransportSockets(Action<Socket> protectAction)
    {
        _protectAction = protectAction;
        _transport?.ProtectSockets(protectAction);
    }
}
