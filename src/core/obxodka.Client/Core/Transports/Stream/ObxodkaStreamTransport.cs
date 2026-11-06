namespace obxodka.Core.Transports;

//транспорт туннеля поверх многолучевого дуплексного http2 потока
public sealed partial class ObxodkaStreamTransport(int serverPort = 443, string? configuredSni = null) : IVpnTransport
{
    //имя активного протокола
    public string ProtocolName => "OBXODKA-STREAM";

    //флаг активности соединения
    public bool IsConnected => Volatile.Read(ref _connected);

    //эффективный рабочий порт сервера
    public int EffectivePort { get; private set; } = serverPort;

    //событие приема входящего сетевого пакета
    public event Action<byte[], int>? OnPacketReceived;

    //событие обновления замера пинга
    public event Action<long>? OnPingUpdated;

    //событие обрыва соединения
    public event Action? OnConnectionDropped;

    //событие согласования или изменения mtu
    public event Action<int>? OnMtuUpdated;

    //глобальный хук создания сокета
    public static Action<Socket>? OnSocketCreated { get; set; }

    //приоритетные двухуровневые очереди отправки для 3 лучей (исключают задержки ack, dns и зондов mtu)
    private readonly PriorityPacketQueue[] _txChannels =
    [
        new(8192),
        new(8192),
        new(8192)
    ];

    //дедупликатор пакетов для подавления повторов при хеджировании
    private readonly PacketDeduplicator _deduplicator = new();

    //инспектор лучей для мониторинга rtt и блэкхолов
    private readonly RayInspector _inspector = new();

    //источник завершения задачи получения ip адресов туннеля
    private TaskCompletionSource<(string ip, string ip6)> _ipTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    //сконфигурированное имя sni
    private readonly string? _configuredSni = configuredSni;

    //заданный порт сервера
    private readonly int _serverPort = serverPort;

    //действие защиты сокетов от зацикливания маршрута
    private Action<Socket>? _protectAction;

    //активное имя sni для маскировки tls
    public string ActiveSni { get; private set; } = AppSecrets.AllowedSniPool[0];

    //профиль маскировки под браузер
    public TlsBrowserProfile ActiveBrowserProfile { get; private set; } = TlsBrowserProfile.Chrome;

    //клиенты и обработчики сокетов для 3 параллельных лучей
    private HttpClient? _httpClient0;
    private SocketsHttpHandler? _handler0;
    private HttpClient? _httpClient1;
    private SocketsHttpHandler? _handler1;
    private HttpClient? _httpClient2;
    private SocketsHttpHandler? _handler2;

    //источник токена отмены текущей сессии
    private CancellationTokenSource? _cts;

    //признак установленного соединения
    private bool _connected;

    //признак освобождения ресурсов
    private bool _disposed;

    //количество активных лучей
    private int _activeRays = 1;

    //признак запуска цикла периодического пинга
    private int _pingLoopStarted;

    //индекс приоритетного луча данных (0 или 1)
    private int _preferredRay;

    //флаг явного предпочтения луча
    private int _hasClearPreferredRay;

    //значения rtt по лучам
    private long _rayRtt0;
    private long _rayRtt1;
    private long _rayRtt2;

    //метки времени последнего приема данных по лучам
    private long _lastRxTicks0;
    private long _lastRxTicks1;
    private long _lastRxTicks2;

    //метка времени последней отправки пакета
    private long _lastTxTicks;

    //метка времени успешного установления туннеля
    private long _connectedAtTicks;

    //активный физический ip адрес сервера
    private string _activeServerIp = string.Empty;

    //токены отмены для отдельных лучей
    private readonly CancellationTokenSource?[] _rayCts = new CancellationTokenSource?[3];

    //счетчики циклов переподключения лучей
    private readonly int[] _rayCycleCount = new int[3];

    //ожидающие подтверждения зонды проверки mtu
    private readonly ConcurrentDictionary<long, TaskCompletionSource<int>> _pendingMtuProbes = new();

    //список полученных тестовых размеров mtu
    private readonly List<int> _receivedBurstMtus = [];

    //назначенный виртуальный ip адрес
    private volatile string? _assignedIp;

    //установка действия защиты сокетов
    public void ProtectSockets(Action<Socket> protectAction) => _protectAction = protectAction;

    //создание http клиента с поддержкой http2, ротации профиля браузера и обхода dpi
    private (HttpClient client, SocketsHttpHandler handler) CreateHttpClient(string targetHost, string serverIp, int port, bool enableDpiBypass = true)
    {
        var browserProfile = ActiveBrowserProfile;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            Proxy = null,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            KeepAlivePingDelay = Timeout.InfiniteTimeSpan,
            KeepAlivePingTimeout = Timeout.InfiniteTimeSpan,
            EnableMultipleHttp2Connections = true,
            InitialHttp2StreamWindowSize = 8388608,
            SslOptions = SslOptionsFactory.Create(
                targetHost: targetHost,
                applicationProtocols: [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
                browserProfile: browserProfile),
            ConnectCallback = async (context, cToken) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                    SendBufferSize = 4194304,
                    ReceiveBufferSize = 4194304
                };
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 3);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
                OnSocketCreated?.Invoke(socket);
                _protectAction?.Invoke(socket);

                var effectivePort = port;
                var connectTarget = IPAddress.TryParse(serverIp, out var ipAddr)
                    ? (EndPoint)new IPEndPoint(ipAddr, effectivePort)
                    : (context.DnsEndPoint ?? new DnsEndPoint(targetHost, effectivePort));

                AppLogger.Log($"[TCP] Dialing socket to {connectTarget} (ServerIp={serverIp}, Port={effectivePort}, Browser={browserProfile})...");
                var sw = Stopwatch.StartNew();
                try
                {
                    await socket.ConnectAsync(connectTarget, cToken).ConfigureAwait(false);
                    sw.Stop();
                    AppLogger.Log($"[TCP OK] Connected to {connectTarget} in {sw.ElapsedMilliseconds}ms.");
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    AppLogger.LogError($"[TCP FAIL] Socket failed to connect to {connectTarget} after {sw.ElapsedMilliseconds}ms: {ex.Message}");
                    socket.Dispose();
                    throw;
                }

                var netStream = new NetworkStream(socket, ownsSocket: true);
                return (enableDpiBypass && IPAddress.TryParse(serverIp, out var parsedIp) && !IPAddress.IsLoopback(parsedIp))
                    ? DpiBypassStream.CreateForProfile(netStream, browserProfile, bypassEnabled: true)
                    : netStream;
            }
        };

        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        return (client, handler);
    }

    //пересоздание http клиента для конкретного луча
    private HttpClient ResetRayHttpClient(int ray, string targetHost, int port, bool enableDpiBypass = true)
    {
        if (ray == 0)
        {
            _httpClient0?.Dispose();
            _handler0?.Dispose();
            (_httpClient0, _handler0) = CreateHttpClient(targetHost, _activeServerIp, port, enableDpiBypass);
            return _httpClient0;
        }

        if (ray == 1)
        {
            _httpClient1?.Dispose();
            _handler1?.Dispose();
            (_httpClient1, _handler1) = CreateHttpClient(targetHost, _activeServerIp, port, enableDpiBypass);
            return _httpClient1;
        }

        _httpClient2?.Dispose();
        _handler2?.Dispose();
        (_httpClient2, _handler2) = CreateHttpClient(targetHost, _activeServerIp, port, enableDpiBypass);
        return _httpClient2;
    }

    //подключение к серверу с перебором портов, пула sni и лестницы браузерных профилей
    public async Task<(string ip, string ip6)> ConnectAsync(string serverIp, string thumbprint, CancellationToken ct)
    {
        _activeServerIp = serverIp;
        var endpointCandidates = SniEngine.BuildStreamCandidates(serverIp, _serverPort, _configuredSni);
        Exception? lastException = null;

        for (var i = 0; i < endpointCandidates.Count; i++)
        {
            var candidate = endpointCandidates[i];
            var targetHost = candidate.TargetHost;
            var port = candidate.Port;
            var enableDpi = candidate.EnableDpiBypass;
            var channelHost = targetHost;
            var activeProfile = candidate.BrowserProfile;
            ActiveBrowserProfile = activeProfile;

            //для резервных браузерных профилей выполняем быструю 1-rtt проверку проходимости raw clienthello через тспу
            if (i > 0 && i + 1 < endpointCandidates.Count && IPAddress.TryParse(serverIp, out var probeIp) && !IPAddress.IsLoopback(probeIp))
            {
                var rawReachable = await RoutingHello.ProbeRawClientHelloAsync(
                    new IPEndPoint(probeIp, port),
                    targetHost,
                    activeProfile,
                    socket =>
                    {
                        OnSocketCreated?.Invoke(socket);
                        _protectAction?.Invoke(socket);
                    },
                    timeoutMs: 900,
                    ct: ct).ConfigureAwait(false);

                if (!rawReachable)
                {
                    _inspector.RecordStep($"1-RTT Raw ClientHello probe blocked for '{targetHost}:{port}' ({activeProfile}) -> rotating browser profile");
                    AppLogger.LogWarning($"[BROWSER-ROTATION] 1-RTT Raw ClientHello probe blocked for '{targetHost}:{port}' ({activeProfile}). Rotating to next browser profile...");
                    continue;
                }
            }

            var attemptTimeoutSeconds = (i == 0 && endpointCandidates.Count > 1) ? 3 : 4;
            var dpiModeStr = enableDpi ? "Clean-Split DPI" : "Direct Stream";
            _inspector.IncrementAttempts();
            _inspector.RecordStep($"Candidate #{i + 1}/{endpointCandidates.Count}: Attempting node {serverIp}:{port} with SNI '{targetHost}' [{dpiModeStr}] Profile: {activeProfile}");
            AppLogger.Log($"[OBXODKA-STREAM] Candidate #{i + 1}/{endpointCandidates.Count}: Attempting node {serverIp}:{port} with SNI '{targetHost}' [{dpiModeStr}] Profile: {activeProfile} (Timeout: {attemptTimeoutSeconds}s)...");

            _cts = new CancellationTokenSource();
            _ipTcs = new TaskCompletionSource<(string ip, string ip6)>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _lastRxTicks0, 0);
            Volatile.Write(ref _lastRxTicks1, 0);
            Volatile.Write(ref _lastRxTicks2, 0);

            var ray1Port = port == 443 ? 8443 : 443;
            var ray2Port = 2053;
            (_httpClient0, _handler0) = CreateHttpClient(targetHost, serverIp, port, enableDpi);
            (_httpClient1, _handler1) = CreateHttpClient(targetHost, serverIp, ray1Port, enableDpi);
            (_httpClient2, _handler2) = CreateHttpClient(targetHost, serverIp, ray2Port, enableDpi);

            using var attemptTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(attemptTimeoutSeconds));
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token, attemptTimeoutCts.Token);

            _ = Task.Run(() => RunPrimaryRayLoopAsync(channelHost, port, thumbprint, enableDpi, _cts.Token), _cts.Token);
            _ = Task.Run(() => RunSecondaryRayAsync(channelHost, ray1Port, thumbprint, enableDpi, _cts.Token), _cts.Token);
            _ = Task.Run(() => RunTertiaryRayAsync(channelHost, ray2Port, thumbprint, enableDpi, _cts.Token), _cts.Token);

            using (handshakeCts.Token.Register(() => _ipTcs.TrySetCanceled()))
            {
                try
                {
                    var result = await _ipTcs.Task.ConfigureAwait(false);
                    ActiveSni = channelHost;
                    _inspector.RecordStep($"Connected node {serverIp}:{EffectivePort} via {channelHost} ({activeProfile})");
                    AppLogger.Log($"[OBXODKA-STREAM] Successfully connected to node physical IP {serverIp}:{EffectivePort} via {channelHost} (Browser: {activeProfile}). Established tunnel assigned IPv4: {result.ip}, IPv6: {result.ip6}");
                    return result;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && i + 1 < endpointCandidates.Count)
                {
                    lastException = ex;
                    var inner = ex.InnerException != null ? $" -> {ex.InnerException.GetType().Name}: {ex.InnerException.Message}" : "";
                    _inspector.RecordStep($"Handshake attempt #{i + 1} failed on '{targetHost}:{port}' [{dpiModeStr}] ({ex.GetType().Name})");
                    AppLogger.LogWarning($"[OBXODKA-STREAM] Handshake attempt #{i + 1} failed on '{targetHost}:{port}' [{dpiModeStr}] ({ex.GetType().Name}: {ex.Message}{inner}). Hopping to next endpoint in pool...");
                    await _cts.CancelAsync().ConfigureAwait(false);
                    _httpClient0?.Dispose();
                    _handler0?.Dispose();
                    _httpClient1?.Dispose();
                    _handler1?.Dispose();
                    _httpClient2?.Dispose();
                    _handler2?.Dispose();
                    await Task.Delay(50, ct).ConfigureAwait(false);
                }
            }
        }

        _inspector.RecordStep($"Exhausted all {endpointCandidates.Count} candidate endpoints");
        _ = _inspector.ReportIncidentIfExhaustedAsync(
            reason: lastException != null ? $"HandshakeExhaustion: {lastException.GetType().Name} - {lastException.Message}" : "HandshakeTimeout",
            targetHost: endpointCandidates.Count > 0 ? endpointCandidates[0].TargetHost : serverIp,
            effectivePort: endpointCandidates.Count > 0 ? endpointCandidates[0].Port : 443,
            currentMtu: SmartMtuEngine.CurrentMtu,
            targetIp: serverIp,
            apiBaseUrl: AppConfig.ApiBaseUrl, cancellationToken: CancellationToken.None);

        throw lastException ?? new TimeoutException("Failed to connect through available SNI and port pool.");
    }
}
