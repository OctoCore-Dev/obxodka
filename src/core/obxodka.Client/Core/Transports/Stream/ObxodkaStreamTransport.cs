namespace obxodka.Core.Transports;

//транспорт туннеля поверх многолучевого дуплексного http2 потока
public sealed partial class ObxodkaStreamTransport(int serverPort = 443, string? configuredSni = null, string? preferredAssignedIp = null)
    : IVpnTransport, IHealthAwareTransport
{
    //дедупликатор пакетов для подавления повторов при хеджировании
    private readonly PacketDeduplicator _deduplicator = new();

    //инспектор лучей для мониторинга rtt и блэкхолов
    private readonly RayInspector _inspector = new();

    //ожидающие подтверждения зонды проверки mtu
    private readonly ConcurrentDictionary<long, TaskCompletionSource<int>?> _pendingMtuProbes = new();

    //ожидающие подтверждения синхронизации mtu от сервера
    private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _pendingMtuSyncAcks = new();

    //токены отмены для отдельных лучей
    private readonly CancellationTokenSource?[] _rayCts = new CancellationTokenSource?[3];

    //счетчики циклов переподключения лучей
    private readonly int[] _rayCycleCount = new int[3];

    //список полученных тестовых размеров mtu
    private readonly List<int> _receivedBurstMtus = [];

    //заданный порт сервера
    private readonly int _serverPort = serverPort;

    //приоритетные двухуровневые очереди отправки для 3 лучей (исключают задержки ack, dns и зондов mtu)
    private readonly PriorityPacketQueue[] _txChannels =
    [
        new(8192),
        new(8192),
        new(8192)
    ];

    //активные фоновые задачи лучей для безопасного ожидания остановки перед освобождением сокетов
    private Task[] _activeRayTasks = [];

    //количество активных лучей
    private int _activeRays = 1;

    //активный физический ip адрес сервера
    private string _activeServerIp = string.Empty;

    //назначенный виртуальный ip адрес
    private volatile string? _assignedIp = preferredAssignedIp;

    //признак установленного соединения
    private bool _connected;

    //метка времени успешного установления туннеля
    private long _connectedAtTicks;

    //источник токена отмены текущей сессии
    private CancellationTokenSource? _cts;

    //признак освобождения ресурсов
    private int _disposed;

    //счетчик лучей завершившихся до завершения рукопожатия
    private int _handshakeRaysFailed;

    //флаг явного предпочтения луча
    private int _hasClearPreferredRay;

    //клиенты для 3 параллельных лучей
    private HttpClient? _httpClient0;
    private HttpClient? _httpClient1;
    private HttpClient? _httpClient2;

    //источник завершения задачи получения ip адресов туннеля
    private TaskCompletionSource<(string ip, string ip6)> _ipTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    //последняя ошибка луча на этапе рукопожатия
    private Exception? _lastHandshakeError;

    //метки времени последнего приема данных по лучам
    private long _lastRxTicks0;
    private long _lastRxTicks1;
    private long _lastRxTicks2;

    //метка времени последней отправки пакета
    private long _lastTxTicks;

    //признак запуска цикла периодического пинга
    private int _pingLoopStarted;

    //индекс приоритетного луча данных (0 или 1)
    private int _preferredRay;

    //действие защиты сокетов от зацикливания маршрута
    private Action<Socket>? _protectAction;

    //эффективный рабочий порт сервера
    public int EffectivePort { get; private set; } = serverPort;

    //текущее число активных лучей многолучевого транспорта
    public int ActiveRayCount => Volatile.Read(ref _activeRays);

    //глобальный хук создания сокета
    public static Action<Socket>? OnSocketCreated { get; set; }

    //активное имя sni для маскировки tls
    public string ActiveSni { get; private set; } = configuredSni ?? string.Empty;

    //профиль маскировки под браузер
    public TlsBrowserProfile ActiveBrowserProfile { get; private set; } = TlsBrowserProfile.Chrome;

    //фактический удаленный ip сокета
    public string? RemoteConnectedEndpointIp => string.IsNullOrWhiteSpace(_activeServerIp) ? null : _activeServerIp;

    //пометка прямого узла как недоступного при сбое или тспу-заморозке
    public void ReportUnhealthyEndpoint() => OctopusEngine.MarkNodeByteCapped(_activeServerIp);

    //имя активного протокола
    public string ProtocolName => "OBXODKA-STREAM";

    //флаг активности соединения
    public bool IsConnected => Volatile.Read(ref _connected);

    //событие приема входящего сетевого пакета
    public event Action<byte[], int>? OnPacketReceived;

    //событие обновления замера пинга
    public event Action<long>? OnPingUpdated;

    //событие обрыва соединения
    public event Action? OnConnectionDropped;

    //событие согласования или изменения mtu
    public event Action<int>? OnMtuUpdated;

    //установка действия защиты сокетов
    public void ProtectSockets(Action<Socket> protectAction) => _protectAction = protectAction;

    //подключение к серверу с перебором портов, пула sni и лестницы браузерных профилей
    public async Task<(string ip, string ip6)> ConnectAsync(string serverIp, string thumbprint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        _activeServerIp = serverIp;
        var endpointCandidates = SniEngine.BuildStreamCandidates(serverIp, _serverPort, configuredSni);
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

            var attemptTimeoutSeconds = 3;
            var dpiModeStr = enableDpi ? "Clean-Split DPI" : "Direct Stream";
            _inspector.IncrementAttempts();
            _inspector.RecordStep(
                $"Candidate #{i + 1}/{endpointCandidates.Count}: Attempting node {serverIp}:{port} with SNI '{targetHost}' [{dpiModeStr}] Profile: {activeProfile}");
            AppLogger.Log(
                $"[OBXODKA-STREAM] Candidate #{i + 1}/{endpointCandidates.Count}: Attempting node {serverIp}:{port} with SNI '{targetHost}' [{dpiModeStr}] Profile: {activeProfile} (Timeout: {attemptTimeoutSeconds}s)...");

            _cts = new CancellationTokenSource();
            _ipTcs = new TaskCompletionSource<(string ip, string ip6)>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _handshakeRaysFailed, 0);
            Volatile.Write(ref _lastHandshakeError, null);
            Volatile.Write(ref _lastRxTicks0, 0);
            Volatile.Write(ref _lastRxTicks1, 0);
            Volatile.Write(ref _lastRxTicks2, 0);

            var ray1Port = port == 443 ? 8443 : 443;
            var ray2Port = 2053;
            _httpClient0 = CreateHttpClient(targetHost, serverIp, port, enableDpi);
            _httpClient1 = CreateHttpClient(targetHost, serverIp, ray1Port, enableDpi);
            _httpClient2 = CreateHttpClient(targetHost, serverIp, ray2Port, enableDpi);

            using var attemptTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(attemptTimeoutSeconds));
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token, attemptTimeoutCts.Token);

            var rayTasks = new[]
            {
                Task.Run(() => RunPrimaryRayLoopAsync(channelHost, port, thumbprint, enableDpi, _cts.Token), _cts.Token),
                Task.Run(() => RunSecondaryRayAsync(channelHost, ray1Port, thumbprint, enableDpi, _cts.Token), _cts.Token),
                Task.Run(() => RunTertiaryRayAsync(channelHost, ray2Port, thumbprint, enableDpi, _cts.Token), _cts.Token)
            };
            _activeRayTasks = rayTasks;

            await using (handshakeCts.Token.Register(() => _ipTcs.TrySetCanceled()))
            {
                try
                {
                    var result = await _ipTcs.Task.ConfigureAwait(false);
                    ActiveSni = channelHost;
                    _inspector.RecordStep($"Connected node {serverIp}:{EffectivePort} via {channelHost} ({activeProfile})");
                    AppLogger.Log(
                        $"[OBXODKA-STREAM] Successfully connected to node physical IP {serverIp}:{EffectivePort} via {channelHost} (Browser: {activeProfile}). Established tunnel assigned IPv4: {result.ip}, IPv6: {result.ip6}");
                    return result;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && i + 1 < endpointCandidates.Count)
                {
                    lastException = ex;
                    var inner = ex.InnerException != null ? $" -> {ex.InnerException.GetType().Name}: {ex.InnerException.Message}" : "";
                    _inspector.RecordStep(
                        $"Handshake attempt #{i + 1} failed on '{targetHost}:{port}' [{dpiModeStr}] ({ex.GetType().Name})");
                    AppLogger.LogWarning(
                        $"[OBXODKA-STREAM] Handshake attempt #{i + 1} failed on '{targetHost}:{port}' [{dpiModeStr}] ({ex.GetType().Name}: {ex.Message}{inner}). Hopping to next endpoint in pool...");
                    var attemptCts = Interlocked.Exchange(ref _cts, null);
                    await attemptCts.CancelAsync().ConfigureAwait(false);

                    try
                    {
                        await Task.WhenAll(rayTasks).WaitAsync(TimeSpan.FromSeconds(1.5), CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        //ожидаемая отмена задач лучей при переходе к следующему кандидату
                    }
                    catch (TimeoutException)
                    {
                        //таймаут ожидания остановки задач лучей при смене кандидата
                    }

                    _activeRayTasks = [];
                    _httpClient0?.Dispose();
                    _httpClient1?.Dispose();
                    _httpClient2?.Dispose();
                    attemptCts.Dispose();
                    await Task.Delay(50, ct).ConfigureAwait(false);
                }
            }
        }

        _inspector.RecordStep($"Exhausted all {endpointCandidates.Count} candidate endpoints");
        _ = _inspector.ReportIncidentIfExhaustedAsync(
            lastException != null ? $"HandshakeExhaustion: {lastException.GetType().Name} - {lastException.Message}" : "HandshakeTimeout",
            endpointCandidates.Count > 0 ? endpointCandidates[0].TargetHost : serverIp,
            endpointCandidates.Count > 0 ? endpointCandidates[0].Port : 443,
            SmartMtuEngine.CurrentMtu,
            serverIp,
            AppConfig.ApiBaseUrl, CancellationToken.None);

        throw lastException ?? new TimeoutException("Failed to connect through available SNI and port pool.");
    }

    //регистрация сбоя луча на этапе рукопожатия для немедленного фейловера без ожидания таймаута
    private void NotifyHandshakeRayFailed(Exception? ex)
    {
        if (ex is not null)
        {
            _ = Interlocked.CompareExchange(ref _lastHandshakeError, ex, null);
        }

        if (_ipTcs.Task.IsCompleted || Interlocked.Increment(ref _handshakeRaysFailed) < 3)
        {
            return;
        }

        var err = Volatile.Read(ref _lastHandshakeError) ?? new SocketException((int)SocketError.ConnectionRefused);
        _ = _ipTcs.TrySetException(err);
    }

    //создание http клиента с поддержкой http2, ротации профиля браузера и обхода dpi
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Владение обработчиком передается в HttpClient (disposeHandler: true) и освобождается в catch при ошибке.")]
    private HttpClient CreateHttpClient(string targetHost, string serverIp, int port, bool enableDpiBypass = true)
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
                targetHost,
                !string.IsNullOrWhiteSpace(OctopusEngine.DynamicSslPublicKeyHash)
                    ? OctopusEngine.DynamicSslPublicKeyHash
                    : OctopusEngine.DirectOriginCertHash,
                [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
                browserProfile),
            ConnectCallback = async (context, cToken) =>
            {
                var effectivePort = port;
                var targetIp = IPAddress.TryParse(serverIp, out var ipAddr)
                    ? ipAddr
                    : await ApiService.ResolveHostAntiPoisonAsync(serverIp, cToken).ConfigureAwait(false);

                var connectTarget = new IPEndPoint(targetIp, effectivePort);
                var socket = new Socket(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                    SendBufferSize = 4194304,
                    ReceiveBufferSize = 4194304
                };
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 3);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
                (_protectAction ?? OnSocketCreated ?? OctopusEngine.Current.ProtectSocket).Invoke(socket);

                AppLogger.Log(
                    $"[TCP] Dialing socket to {connectTarget} (ServerIp={serverIp}, Port={effectivePort}, Browser={browserProfile})...");
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
                    AppLogger.LogError(
                        $"[TCP FAIL] Socket failed to connect to {connectTarget} after {sw.ElapsedMilliseconds}ms: {ex.Message}");
                    socket.Dispose();
                    throw;
                }

                var netStream = new NetworkStream(socket, true);
                return enableDpiBypass && !IPAddress.IsLoopback(targetIp)
                    ? DpiBypassStream.CreateForProfile(netStream, browserProfile)
                    : netStream;
            }
        };

        try
        {
            return new HttpClient(handler, true)
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
        }
        catch
        {
            handler.Dispose();
            throw;
        }
    }

    //пересоздание http клиента для конкретного луча
    private HttpClient ResetRayHttpClient(int ray, string targetHost, int port, bool enableDpiBypass = true)
    {
        switch (ray)
        {
            case 0:
                _httpClient0?.Dispose();
                _httpClient0 = CreateHttpClient(targetHost, _activeServerIp, port, enableDpiBypass);
                _httpClient0.Timeout = Timeout.InfiniteTimeSpan;
                return _httpClient0;
            case 1:
                _httpClient1?.Dispose();
                _httpClient1 = CreateHttpClient(targetHost, _activeServerIp, port, enableDpiBypass);
                _httpClient1.Timeout = Timeout.InfiniteTimeSpan;
                return _httpClient1;
        }

        _httpClient2?.Dispose();
        _httpClient2 = CreateHttpClient(targetHost, _activeServerIp, port, enableDpiBypass);
        _httpClient2.Timeout = Timeout.InfiniteTimeSpan;
        return _httpClient2;
    }
}
