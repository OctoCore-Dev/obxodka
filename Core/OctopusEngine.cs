namespace obxodka.Core;

internal sealed partial class OctopusEngine : IDisposable, IAsyncDisposable
{
    private static readonly Lazy<OctopusEngine> t_instance = new(
        () => new OctopusEngine(),
        LazyThreadSafetyMode.ExecutionAndPublication
    );
    public static OctopusEngine Current => t_instance.Value;

    public static MeshRelayServer? ActiveRelayServer => MeshRelayManager.ActiveRelayServer;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance")]
    private IVpnTransport? _transport;
    private X509Certificate2? _clientCert;
    private string? _jwtToken;
    private CancellationTokenSource? _cts;
    public static string? DynamicSslPublicKeyHash { get; set; }

    public int ActiveRays { get; private set; } = 1;

    public bool IsConnected => _transport is { IsConnected: true };
    public string AssignedIp { get; private set; } = "10.8.0.2";
    public string AssignedIpV6 { get; private set; } = "fd00::2";

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

    public string ActiveProtocol { get; private set; } = "FECHSUE";

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

#if ANDROID || IOS
        ActiveRays = Preferences.Get("BatteryMode", 2);
#else
        ActiveRays = Preferences.Get("BatteryMode", PacketRouter.MaxRays);
#endif

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
                    ("FECHSUE", () => new FechsueTransport()),
                    ("HTTP3", () => new GrpcTransport(useHttp3: true, activeRays: ActiveRays, clientCert: _clientCert, jwtToken: _jwtToken, serverPort: serverPort)),
                    ("HTTP2", () => new GrpcTransport(useHttp3: false, activeRays: ActiveRays, clientCert: _clientCert, jwtToken: _jwtToken, serverPort: serverPort))
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
                    probeCts.CancelAfter(TimeSpan.FromSeconds(3));

                    probeTransport.OnPacketReceived += (pkt, len) =>
                    {
                        Interlocked.Add(ref _totalBytesReceived, len);
                        OnPacketReceived?.Invoke(pkt, len);
                    };
                    probeTransport.OnPingUpdated += ping => OnPingUpdated?.Invoke(ping);
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
                "FECHSUE" when meshRelay == null => new FechsueTransport(),
                "HTTP3" when meshRelay == null => new GrpcTransport(useHttp3: true, activeRays: ActiveRays, clientCert: _clientCert, jwtToken: _jwtToken, serverPort: serverPort),
                "HTTP2" or "GRPC" or _ => new GrpcTransport(useHttp3: false, activeRays: ActiveRays, clientCert: _clientCert, jwtToken: _jwtToken, serverPort: serverPort, meshRelay: meshRelay)
            };

            _transport = transport;
            ActiveProtocol = protocolMode;
            transport.OnPacketReceived += (pkt, len) =>
            {
                Interlocked.Add(ref _totalBytesReceived, len);
                OnPacketReceived?.Invoke(pkt, len);
            };
            transport.OnPingUpdated += ping => OnPingUpdated?.Invoke(ping);
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

    private void StartTrafficMonitor()
    {
        _ = Task.Run(async () =>
        {
            long lastSent = 0;
            long lastReceived = 0;
            var deadTicks = 0;

            try
            {
                while (_cts is { Token.IsCancellationRequested: false })
                {
                    var currentSent = TotalBytesSent;
                    var currentReceived = TotalBytesReceived;
                    OnTrafficUpdated?.Invoke(currentSent, currentReceived);

                    if (currentSent > lastSent && currentReceived == lastReceived)
                    {
                        deadTicks++;
                        if (deadTicks >= 150)
                        {
                            Debug.WriteLine("[ENGINE] Dead connection detected (30s without RX while TX).");
                            OnDeadConnectionDetected?.Invoke();
                            break;
                        }
                    }
                    else if (currentReceived > lastReceived)
                    {
                        deadTicks = 0;
                    }

                    lastSent = currentSent;
                    lastReceived = currentReceived;
                    await Task.Delay(200, _cts.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    public Task SendPacketAsync(byte[] packet)
    {
        if (!IsConnected || _transport is null)
        {
            return Task.CompletedTask;
        }

        _ = Interlocked.Add(ref _totalBytesSent, packet.Length);
        _transport.SendPacketFromPool(packet, packet.Length);
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
