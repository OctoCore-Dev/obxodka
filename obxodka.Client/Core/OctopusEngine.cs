namespace obxodka.Core;

public sealed partial class OctopusEngine : IDisposable, IAsyncDisposable
{
    private static readonly Lazy<OctopusEngine> t_instance = new(
        () => new OctopusEngine(),
        LazyThreadSafetyMode.ExecutionAndPublication
    );
    public static OctopusEngine Current => t_instance.Value;

    public static MeshRelayServer? ActiveRelayServer => MeshRelayManager.ActiveRelayServer;

    [SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance")]
    private IVpnTransport? _transport;
    private X509Certificate2? _clientCert;
    private string? _jwtToken;
    private CancellationTokenSource? _cts;
    public static string? DynamicSslPublicKeyHash { get; set; }

    public int ActiveRays { get; private set; } = 1;

    public bool IsConnected => _transport is { IsConnected: true };
    public string AssignedIp { get; private set; } = "10.8.0.2";
    public string AssignedIpV6 { get; private set; } = "fd00::2";
    public string? PublicWanIp { get; set; }

    public event Action<byte[], int>? OnPacketReceived;
    public event Action? OnConnectionDropped;
    public event Action? OnDeadConnectionDetected;
    public event Action<long, long>? OnTrafficUpdated;
    public event Action<long>? OnPingUpdated;
    public static event Action<string>? OnCertificateRevoked;

    private long _totalBytesSent;
    private long _totalBytesReceived;
    public long TotalBytesSent => Interlocked.Read(ref _totalBytesSent);
    public long TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);

    public string ActiveProtocol { get; private set; } = "AUTO";

    private long _trafficStartTicks = Environment.TickCount64;

    public void ResetTrafficCounters()
    {
        _ = Interlocked.Exchange(ref _totalBytesSent, 0);
        _ = Interlocked.Exchange(ref _totalBytesReceived, 0);
        Volatile.Write(ref _trafficStartTicks, Environment.TickCount64);
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
        else
        {
            var alpha = rawRtt > _smoothedPing * 2.0 ? 0.05 : 0.25;
            _smoothedPing = (_smoothedPing * (1 - alpha)) + (rawRtt * alpha);
        }
        OnPingUpdated?.Invoke((long)Math.Round(_smoothedPing));
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
            _clientCert = X509CertificateLoader.LoadPkcs12(certBytes, AppSecrets.InternalPfxPassword, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        }
        catch
        {
            Debug.WriteLine("[VPN] Обнаружен устаревший сертификат. Требуется повторная авторизация.");
            OnCertificateRevoked?.Invoke("Обнаружен устаревший сертификат. Пожалуйста, войдите снова.");
            throw new UnauthorizedAccessException("Old certificate");
        }

        _jwtToken = session.JwtToken;
        _cts = new CancellationTokenSource();

        var defaultRays = DeviceInfo.Platform is "Android" or "iOS" ? 2 : PacketRouter.MaxRays;
        var configuredRays = Preferences.Get("BatteryMode", defaultRays);
        ActiveRays = configuredRays is >= 1 and <= PacketRouter.MaxRays ? configuredRays : defaultRays;

        MeshRelayInfo? meshRelay = null;
        if (MeshSettings.MeshEnabled)
        {
            try
            {
                meshRelay = await MeshRelayClient.GetBestRelayAsync(_jwtToken, _cts.Token).ConfigureAwait(false);
                if (meshRelay != null)
                {
                    Debug.WriteLine($"[MESH] Routing traffic through Relay: {meshRelay.IpAddress}:{meshRelay.Port} ({meshRelay.CountryCode})");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MESH] Relay lookup failed: {ex.Message}. Falling back to direct connection.");
            }
        }

        var protocolMode = Preferences.Get("ProtocolMode", "AUTO");

        if (protocolMode == "AUTO")
        {
            (string name, Func<IVpnTransport> factory)[] candidates = meshRelay != null
                ?
                [
                    ("HTTP2", () => new GrpcTransport(useHttp3: false, activeRays: ActiveRays, clientCert: _clientCert, jwtToken: _jwtToken, serverPort: serverPort, meshRelay: meshRelay))
                ]
                :
                [
                    ("HTTP2", () => new GrpcTransport(useHttp3: false, activeRays: ActiveRays, clientCert: _clientCert, jwtToken: _jwtToken, serverPort: serverPort)),
                    ("FECHSUE", () => new FechsueTransport(activeRays: ActiveRays))
                ];

            Exception? lastError = null;
            foreach (var (pName, factory) in candidates)
            {
                if (_cts.IsCancellationRequested)
                {
                    break;
                }

                var probeTransport = factory();
                try
                {
                    Debug.WriteLine($"[AUTO PROTOCOL] Probing {pName}...");
                    using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    probeCts.CancelAfter(TimeSpan.FromSeconds(pName == "FECHSUE" ? 3 : 8));

                    probeTransport.OnPacketReceived += (pkt, len) =>
                    {
                        _ = Interlocked.Add(ref _totalBytesReceived, len);
                        OnPacketReceived?.Invoke(pkt, len);
                    };
                    probeTransport.OnPingUpdated += ReportPing;
                    probeTransport.OnConnectionDropped += () => OnConnectionDropped?.Invoke();

                    var (ip, ip6) = await probeTransport.ConnectAsync(serverIp, _clientCert?.Thumbprint ?? "", probeCts.Token);
                    _transport = probeTransport;
                    ActiveProtocol = pName;
                    AssignedIp = ip;
                    AssignedIpV6 = ip6;
                    Debug.WriteLine($"[AUTO PROTOCOL] Connected successfully with {pName} -> {ip}");
                    StartTrafficMonitor();
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AUTO PROTOCOL] {pName} probe failed: {ex.Message}. Falling back...");
                    lastError = ex;
                    await probeTransport.DisposeAsync();
                }
            }

            throw new InvalidOperationException($"Не удалось подключиться ни по одному из протоколов: {lastError?.Message}", lastError);
        }
        else
        {
            IVpnTransport transport = protocolMode switch
            {
                "FECHSUE" when meshRelay == null => new FechsueTransport(activeRays: ActiveRays),
                "HTTP2" or "HTTP3" or "GRPC" or _ => new GrpcTransport(useHttp3: false, activeRays: ActiveRays, clientCert: _clientCert, jwtToken: _jwtToken, serverPort: serverPort, meshRelay: meshRelay)
            };

            _transport = transport;
            ActiveProtocol = protocolMode;
            transport.OnPacketReceived += (pkt, len) =>
            {
                _ = Interlocked.Add(ref _totalBytesReceived, len);
                OnPacketReceived?.Invoke(pkt, len);
            };
            transport.OnPingUpdated += ReportPing;
            transport.OnConnectionDropped += () => OnConnectionDropped?.Invoke();

            var (ip, ip6) = await transport.ConnectAsync(serverIp, _clientCert?.Thumbprint ?? "", _cts.Token);
            AssignedIp = ip;
            AssignedIpV6 = ip6;
            StartTrafficMonitor();
        }
    }

    public async Task ReconnectAsync(string serverIp, int serverPort)
    {
        await DisposeAsync();
        await ConnectAsync(serverIp, serverPort);
    }

    public async Task<bool> VerifyDownlinkAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (!IsConnected || _transport is null)
        {
            Debug.WriteLine("[VERIFY] Verification skipped: transport is not connected.");
            return false;
        }

        var initialReceived = TotalBytesReceived;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts?.Token ?? CancellationToken.None);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnPacket(byte[] pkt, int len)
        {
            if (len > 0)
            {
                Debug.WriteLine($"[VERIFY] Inbound packet acknowledged ({len} bytes)! Downlink confirmed.");
                _ = tcs.TrySetResult(true);
            }
        }

        void OnPing(long rtt)
        {
            Debug.WriteLine($"[VERIFY] Ping probe acknowledged (RTT={rtt}ms)! Downlink confirmed.");
            _ = tcs.TrySetResult(true);
        }

        OnPacketReceived += OnPacket;
        OnPingUpdated += OnPing;

        Debug.WriteLine($"[VERIFY-START] Verifying downlink. Timeout={timeout.TotalMilliseconds}ms, InitialRX={initialReceived}");

        try
        {
            async Task SendProbesAsync()
            {
                try
                {
                    Debug.WriteLine("[VERIFY-PROBE] Dispatching internal Ping probe (0x99)...");
                    await (_transport?.SendPingProbeAsync() ?? Task.CompletedTask);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[VERIFY-PROBE-ERR] Ping probe failed: {ex.Message}");
                }

                try
                {
                    if (BuildIcmpProbePacket(AssignedIp, "100.64.0.1") is { } pIcmpGw)
                    {
                        _ = SendPacketAsync(pIcmpGw);
                    }
                    if (BuildIcmpProbePacket(AssignedIp, "8.8.8.8") is { } pIcmpExt)
                    {
                        _ = SendPacketAsync(pIcmpExt);
                    }
                    if (BuildDnsProbePacket(AssignedIp, "77.88.8.8") is { } pYandex)
                    {
                        _ = SendPacketAsync(pYandex);
                    }
                    if (BuildDnsProbePacket(AssignedIp, "1.1.1.1") is { } p1)
                    {
                        _ = SendPacketAsync(p1);
                    }
                    if (BuildDnsProbePacket(AssignedIp, "8.8.8.8") is { } p8)
                    {
                        _ = SendPacketAsync(p8);
                    }
                    Debug.WriteLine($"[VERIFY-PROBES-SENT] Probes dispatched. Current TX={TotalBytesSent}B, RX={TotalBytesReceived}B");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[VERIFY-PROBE-ERR] Packet probes failed: {ex.Message}");
                }
            }

            _ = Task.Run(SendProbesAsync, linkedCts.Token);

            var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < deadline && !linkedCts.IsCancellationRequested)
            {
                if (TotalBytesReceived > initialReceived)
                {
                    Debug.WriteLine($"[VERIFY-SUCCESS] Received bytes increased: {TotalBytesReceived - initialReceived} B (TotalRX={TotalBytesReceived})");
                    return true;
                }

                var delayTask = Task.Delay(350, linkedCts.Token);
                var completed = await Task.WhenAny(tcs.Task, delayTask).ConfigureAwait(false);
                if (completed == tcs.Task && await tcs.Task.ConfigureAwait(false))
                {
                    Debug.WriteLine($"[VERIFY-SUCCESS] Probe event completed! TotalBytesReceived={TotalBytesReceived}");
                    return true;
                }

                _ = Task.Run(SendProbesAsync, linkedCts.Token);
            }

            var success = TotalBytesReceived > initialReceived;
            Debug.WriteLine($"[VERIFY-DONE] Completed. Result={success}, InitialRX={initialReceived}, CurrentRX={TotalBytesReceived}");
            return success;
        }
        catch (OperationCanceledException)
        {
            var success = TotalBytesReceived > initialReceived;
            Debug.WriteLine($"[VERIFY-CANCEL] Canceled. Result={success}");
            return success;
        }
        finally
        {
            OnPacketReceived -= OnPacket;
            OnPingUpdated -= OnPing;
        }
    }

    private static byte[]? BuildIcmpProbePacket(string assignedIp, string targetIp = "100.64.0.1")
    {
        if (!IPAddress.TryParse(assignedIp, out var srcIp) || srcIp.AddressFamily != AddressFamily.InterNetwork ||
            !IPAddress.TryParse(targetIp, out var dstIp) || dstIp.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        const int totalLen = 28;
        var packet = new byte[totalLen];

        packet[0] = 0x45;
        packet[1] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), totalLen);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), 0x7788);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), 0x4000);
        packet[8] = 64;
        packet[9] = 1; // ICMP
        srcIp.GetAddressBytes().CopyTo(packet.AsSpan(12, 4));
        dstIp.GetAddressBytes().CopyTo(packet.AsSpan(16, 4));

        var ipChecksum = ComputeIpChecksum(packet.AsSpan(0, 20));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10, 2), ipChecksum);

        // ICMP Echo Request (Type 8, Code 0)
        packet[20] = 8;
        packet[21] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(24, 2), 0x1337);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(26, 2), 1);

        var icmpChecksum = ComputeIpChecksum(packet.AsSpan(20, 8));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), icmpChecksum);

        return packet;
    }

    private static byte[]? BuildDnsProbePacket(string assignedIp, string targetDnsIp = "1.1.1.1")
    {
        if (!IPAddress.TryParse(assignedIp, out var srcIp) || srcIp.AddressFamily != AddressFamily.InterNetwork ||
            !IPAddress.TryParse(targetDnsIp, out var dstIp) || dstIp.AddressFamily != AddressFamily.InterNetwork)
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
        dstIp.GetAddressBytes().CopyTo(packet.AsSpan(16, 4));

        var ipChecksum = ComputeIpChecksum(packet.AsSpan(0, 20));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10, 2), ipChecksum);

        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20, 2), 53535);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), 53);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(24, 2), (ushort)udpLen);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(26, 2), 0);

        dns.CopyTo(packet.AsSpan(28));
        return packet;
    }

    private static ushort ComputeIpChecksum(ReadOnlySpan<byte> header)
    {
        uint sum = 0;
        for (var i = 0; i < header.Length; i += 2)
        {
            if (i == 10)
            {
                continue;
            }
            sum += BinaryPrimitives.ReadUInt16BigEndian(header.Slice(i, 2));
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
            var deadTicks = 0;
            long heartbeatCount = 0;
            Volatile.Write(ref _trafficStartTicks, Environment.TickCount64);

            void OnPing(long _) => Volatile.Write(ref deadTicks, 0);
            OnPingUpdated += OnPing;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var currentSent = TotalBytesSent;
                    var currentReceived = TotalBytesReceived;
                    OnTrafficUpdated?.Invoke(currentSent, currentReceived);

                    heartbeatCount++;
                    if (heartbeatCount % 5 == 0) // Every 1000ms
                    {
                        var startTicks = Volatile.Read(ref _trafficStartTicks);
                        var elapsedMs = Environment.TickCount64 - startTicks;
                        Debug.WriteLine($"[MONITOR-HEARTBEAT] TX={currentSent}B, RX={currentReceived}B, DeadTicks={deadTicks}, Elapsed={elapsedMs}ms, Proto={ActiveProtocol}");
                    }

                    if (currentSent > lastSent && currentReceived == lastReceived)
                    {
                        deadTicks++;
                        var startTicks = Volatile.Read(ref _trafficStartTicks);
                        var elapsedMs = Environment.TickCount64 - startTicks;

                        // Safety margin: do not declare a blackhole during first 60 seconds of connection
                        var isInitialBlackhole = elapsedMs is >= 60000 and < 180000 && currentSent > 50000 && currentReceived == 0;

                        var isDead = (isInitialBlackhole && deadTicks >= 100) ||
                                     (elapsedMs >= 60000 && currentSent > 100000 && currentReceived == 0 && deadTicks >= 150) ||
                                     deadTicks >= 200;

                        if (isDead)
                        {
                            Debug.WriteLine($"[ENGINE] Dead connection detected. InitialBlackhole={isInitialBlackhole}, TX={currentSent}, RX={currentReceived}, DeadTicks={deadTicks}, ElapsedMs={elapsedMs}");
                            OnDeadConnectionDetected?.Invoke();
                            break;
                        }
                    }
                    else if (currentReceived > lastReceived)
                    {
                        deadTicks = 0;
                    }
                    else if (currentSent == lastSent && deadTicks > 0)
                    {
                        deadTicks--;
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
                Debug.WriteLine("[MONITOR] Traffic monitor thread terminated.");
            }
        }, token);
    }

    public Task SendPacketAsync(byte[] packet)
    {
        if (!IsConnected || _transport is null || packet.Length == 0)
        {
            return Task.CompletedTask;
        }

        var poolBuf = ArrayPool<byte>.Shared.Rent(packet.Length);
        Buffer.BlockCopy(packet, 0, poolBuf, 0, packet.Length);
        _ = Interlocked.Add(ref _totalBytesSent, packet.Length);
        _transport.SendPacketFromPool(poolBuf, packet.Length);
        return Task.CompletedTask;
    }

    public Task SendPacketFromPoolAsync(byte[] inputBuf, int length)
    {
        if (!IsConnected || _transport is null)
        {
            ArrayPool<byte>.Shared.Return(inputBuf);
            return Task.CompletedTask;
        }

        _ = Interlocked.Add(ref _totalBytesSent, length);
        _transport.SendPacketFromPool(inputBuf, length);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _smoothedPing = 0;
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
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _clientCert?.Dispose();
        _clientCert = null;

        _transport?.Dispose();
        _transport = null;

        GC.SuppressFinalize(this);
    }

    public static Task StartRelayIfEnabledAsync() => MeshRelayManager.StartRelayIfEnabledAsync();

    public static Task StopRelayAsync() => MeshRelayManager.StopRelayAsync();

    public void ProtectTransportSockets(Action<Socket> protectAction) => _transport?.ProtectSockets(protectAction);
}
