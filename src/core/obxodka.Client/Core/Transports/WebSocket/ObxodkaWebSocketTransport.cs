namespace obxodka.Core.Transports;

//транспорт туннеля поверх защищенного websocket соединения
public sealed partial class ObxodkaWebSocketTransport(int serverPort = 443, string? configuredSni = null, string? preferredAssignedIp = null) : IVpnTransport
{
    //имя активного протокола
    public string ProtocolName => "OBXODKA-WS";

    //флаг активности соединения
    public bool IsConnected => Volatile.Read(ref _connected);

    //эффективный рабочий порт сервера
    public int EffectivePort { get; private set; } = serverPort;

    //активное имя sni для маскировки tls рукопожатия
    public string ActiveSni { get; private set; } = configuredSni ?? AppConfig.PrimaryDomain;

    //профиль маскировки под браузер
    public TlsBrowserProfile ActiveBrowserProfile { get; private set; } = TlsBrowserProfile.Chrome;

    //событие приема входящего сетевого пакета
    public event Action<byte[], int>? OnPacketReceived;

    //событие обновления замера пинга
    public event Action<long>? OnPingUpdated;

    //событие обрыва соединения
    public event Action? OnConnectionDropped;

    //событие согласования или изменения mtu
    public event Action<int>? OnMtuUpdated;

    //приоритетная очередь исходящих пакетов для строго однопоточной отправки без bufferbloat
    private readonly PriorityPacketQueue _txQueue = new(16384);

    //источник завершения задачи получения ip адресов туннеля
    private TaskCompletionSource<(string ip, string ip6)> _ipTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    //сконфигурированное имя sni
    private readonly string? _configuredSni = configuredSni;

    //заданный порт сервера
    private readonly int _serverPort = serverPort;

    //действие защиты системного сокета от петли маршрутизации
    private Action<Socket>? _protectAction;

    //активный клиентский websocket
    private ClientWebSocket? _webSocket;

    //обработчик http вызовов для сокета websocket
    private HttpMessageInvoker? _wsInvoker;

    //источник токена отмены текущей сессии
    private CancellationTokenSource? _cts;

    //активные фоновые задачи чтения, записи и пинга сокета
    private Task[] _activeLoopTasks = [];

    //признак установленного соединения
    private bool _connected;

    //признак освобождения ресурсов
    private bool _disposed;

    //активный целевой хост сервера
    private string _activeServerHost = AppConfig.PrimaryDomain;

    //назначенный клиенту виртуальный ip адрес
    private volatile string? _assignedIp = preferredAssignedIp;

    //сессионный ключ маскировки фреймов
    private uint _sessionKey;

    //кандидатный сессионный ключ на этапе рукопожатия
    private uint _candidateSessionKey;

    //ожидающие подтверждения зонды проверки исходящего mtu
    private readonly ConcurrentDictionary<long, TaskCompletionSource<int>> _pendingMtuProbes = new();

    //список полученных тестовых размеров mtu
    private readonly List<int> _receivedBurstMtus = [];

    //глобальный хук создания сокета
    public static Action<Socket>? OnSocketCreated { get; set; }

    //установка действия защиты сокетов
    public void ProtectSockets(Action<Socket> protectAction) => _protectAction = protectAction;

    //создание обработчика сетевых запросов с защитой сокета, ротацией браузера и обходом dpi
    [SuppressMessage("Reliability", "CA2000:Ликвидировать объекты перед потерей области", Justification = "HttpMessageInvoker принимает владение handler")]
    private HttpMessageInvoker CreateWsInvoker(string targetSni, string serverIp, int port)
    {
        var browserProfile = ActiveBrowserProfile;
        var isDirectIp = IPAddress.TryParse(serverIp, out var literalIp);
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            Proxy = null,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions = SslOptionsFactory.Create(
                targetHost: targetSni,
                dynamicPinningHash: string.Empty,
                browserProfile: browserProfile),
            ConnectCallback = async (context, cToken) =>
            {
                var candidateIps = isDirectIp && literalIp is not null
                    ? [literalIp]
                    : await ApiService.ResolveAllHostsAntiPoisonAsync(targetSni, cToken).ConfigureAwait(false);

                Socket? connectedSocket = null;
                Exception? lastSocketEx = null;

                for (var ipIdx = 0; ipIdx < candidateIps.Length; ipIdx++)
                {
                    var targetIp = candidateIps[ipIdx];
                    var socket = new Socket(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true,
                        SendBufferSize = 4194304,
                        ReceiveBufferSize = 4194304
                    };
                    OnSocketCreated?.Invoke(socket);
                    ObxodkaStreamTransport.OnSocketCreated?.Invoke(socket);
                    _protectAction?.Invoke(socket);

                    try
                    {
                        if (candidateIps.Length > 1 && ipIdx + 1 < candidateIps.Length)
                        {
                            using var perIpCts = CancellationTokenSource.CreateLinkedTokenSource(cToken);
                            perIpCts.CancelAfter(TimeSpan.FromMilliseconds(1800));
                            await socket.ConnectAsync(new IPEndPoint(targetIp, port), perIpCts.Token).ConfigureAwait(false);
                        }
                        else
                        {
                            await socket.ConnectAsync(new IPEndPoint(targetIp, port), cToken).ConfigureAwait(false);
                        }

                        connectedSocket = socket;
                        break;
                    }
                    catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or TimeoutException or ObjectDisposedException || cToken.IsCancellationRequested)
                    {
                        lastSocketEx = ex;
                        socket.Dispose();
                        if (cToken.IsCancellationRequested)
                        {
                            throw;
                        }
                    }
                }

                if (connectedSocket is null)
                {
                    ApiService.InvalidateHostCache(targetSni);
                    throw lastSocketEx ?? new SocketException((int)SocketError.HostUnreachable);
                }

                var netStream = new NetworkStream(connectedSocket, ownsSocket: true);
                var isLoopback = connectedSocket.RemoteEndPoint is IPEndPoint remoteEp && IPAddress.IsLoopback(remoteEp.Address);
                return !isLoopback
                    ? DpiBypassStream.CreateForProfile(netStream, browserProfile, bypassEnabled: true, forcePureL4: true)
                    : netStream;
            }
        };

        return new HttpMessageInvoker(handler, disposeHandler: true);
    }

    //подключение к серверу через цепочку портов, зеркал и браузерных профилей сформированную sni движком
    public async Task<(string ip, string ip6)> ConnectAsync(string serverIp, string thumbprint, CancellationToken ct)
    {
        _activeServerHost = !string.IsNullOrWhiteSpace(_configuredSni)
            ? _configuredSni
            : (!IPAddress.TryParse(serverIp, out _) ? serverIp : AppConfig.PrimaryDomain);
        ActiveSni = _activeServerHost;

        var candidates = SniEngine.BuildWebSocketCandidates(serverIp, _serverPort, _configuredSni);
        Exception? lastEx = null;

        for (var pIdx = 0; pIdx < candidates.Count; pIdx++)
        {
            var candidate = candidates[pIdx];
            var candidatePort = candidate.Port;
            var candidateServerIp = candidate.ServerHost;
            var candidateSni = candidate.TargetSni;
            var targetHostHeader = candidate.HostHeader;
            var connectHost = candidate.ConnectHost;
            var wsPath = candidate.Path;
            EffectivePort = candidatePort;
            ActiveSni = candidateSni;
            ActiveBrowserProfile = candidate.BrowserProfile;

            _cts = new CancellationTokenSource();
            _ipTcs = new TaskCompletionSource<(string ip, string ip6)>(TaskCreationOptions.RunContinuationsAsynchronously);

            ClientWebSocket? ws = null;
            HttpMessageInvoker? invoker = null;
            CancellationTokenSource? attemptCts = null;
            CancellationTokenSource? linkedCts = null;
            var ownershipTransferred = false;

            try
            {
                ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                ws.Options.SetBuffer(131072, 131072);
                try
                {
                    ws.Options.SetRequestHeader("Host", targetHostHeader);
                }
                catch (PlatformNotSupportedException)
                {
                    //игнорируем неподдерживаемую платформой ручную установку заголовка host
                }
                catch (ArgumentException)
                {
                    //игнорируем ошибку формата заголовка host
                }
                RoutingHello.ApplyWebSocketHeaders(ws.Options, ActiveBrowserProfile);

                var cid = RandomNumberGenerator.GetInt32(10000000, 100000000);
                var nowQuerySec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var uriBuilder = new UriBuilder("wss", connectHost, candidatePort, wsPath)
                {
                    Query = $"cid={cid}&_t={nowQuerySec}&v=133.0"
                };

                invoker = CreateWsInvoker(candidateSni, candidateServerIp, candidatePort);
                attemptCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(8000));
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token, attemptCts.Token);

                AppLogger.Log($"[OBXODKA-WS] Connecting to {uriBuilder.Uri} (SNI={ActiveSni}, Port={candidatePort}, Profile={ActiveBrowserProfile})...");
                await ws.ConnectAsync(uriBuilder.Uri, invoker, linkedCts.Token).ConfigureAwait(false);

                _wsInvoker?.Dispose();
                _wsInvoker = invoker;
                _webSocket = ws;
                ownershipTransferred = true;

                var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var sessionSeed = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
                _candidateSessionKey = ObxodkaFraming.DeriveSessionKey(thumbprint, 0, sessionSeed);
                Volatile.Write(ref _sessionKey, 0u);
                var hsProof = ObxodkaFraming.ComputeHandshakeProof(thumbprint, 0, sessionSeed, nowSec);
                var handshakeStr = string.IsNullOrEmpty(_assignedIp)
                    ? $"{thumbprint}|R:0|M:{SmartMtuEngine.CurrentMtu}|K:{sessionSeed:X8}|T:{nowSec}|P:{hsProof}"
                    : $"{thumbprint}|{_assignedIp}|R:0|M:{SmartMtuEngine.CurrentMtu}|K:{sessionSeed:X8}|T:{nowSec}|P:{hsProof}";
                var hsPayload = Encoding.UTF8.GetBytes(handshakeStr);
                var hsFrame = ObxodkaFraming.Pack(ObxodkaFraming.CmdHandshake, hsPayload, out var hsTotalLen);
                try
                {
                    await ws.SendAsync(hsFrame.AsMemory(0, hsTotalLen), WebSocketMessageType.Binary, true, linkedCts.Token).ConfigureAwait(false);
                    var chaffFrame = ObxodkaFraming.Pack(ObxodkaFraming.CmdChaff, [], out var chaffLen, SmartMtuEngine.CurrentMtu, _candidateSessionKey);
                    try
                    {
                        await ws.SendAsync(chaffFrame.AsMemory(0, chaffLen), WebSocketMessageType.Binary, true, linkedCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(chaffFrame);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(hsFrame);
                }

                var readerTask = Task.Run(() => ReaderLoopAsync(ws, _cts.Token), _cts.Token);
                var writerTask = Task.Run(() => WriterLoopAsync(ws, _cts.Token), _cts.Token);
                _activeLoopTasks = [readerTask, writerTask];

                using (linkedCts.Token.Register(() => _ipTcs.TrySetCanceled()))
                {
                    var result = await _ipTcs.Task.ConfigureAwait(false);
                    Volatile.Write(ref _connected, true);
                    var pingTask = Task.Run(() => PingLoopAsync(_cts.Token), _cts.Token);
                    _activeLoopTasks = [readerTask, writerTask, pingTask];
                    _ = SendPingProbeAsync();
                    AppLogger.Log($"[OBXODKA-WS] Connected on port {candidatePort} (Browser: {ActiveBrowserProfile})! Assigned IPv4: {result.ip}, IPv6: {result.ip6}");
                    return result;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && pIdx + 1 < candidates.Count)
            {
                lastEx = ex;
                Volatile.Write(ref _connected, false);
                _txQueue.DrainAndReturn(static buf => ArrayPool<byte>.Shared.Return(buf));
                AppLogger.LogWarning($"[OBXODKA-WS] Port {candidatePort} (Profile {ActiveBrowserProfile}) failed: {ex.Message}. Trying next candidate...");
                var retryCts = Interlocked.Exchange(ref _cts, null);
                var loopTasks = _activeLoopTasks;
                _activeLoopTasks = [];
                try
                {
                    if (retryCts is not null)
                    {
                        await retryCts.CancelAsync().ConfigureAwait(false);
                    }
                    if (loopTasks.Length > 0)
                    {
                        await Task.WhenAll(loopTasks).WaitAsync(TimeSpan.FromSeconds(1.5), CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    //подавление ожидаемой отмены фоновых задач попытки
                }
                catch (TimeoutException)
                {
                    //подавление таймаута ожидания фоновых задач при смене порта
                }
                retryCts?.Dispose();
                _webSocket = null;
                _wsInvoker = null;
                ownershipTransferred = false;
                try
                {
                    ws?.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    //сокет уже освобожден
                }
                try
                {
                    invoker?.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    //обработчик уже освобожден
                }
                ws = null;
                invoker = null;
            }
            finally
            {
                linkedCts?.Dispose();
                attemptCts?.Dispose();
                if (!ownershipTransferred)
                {
                    ws?.Dispose();
                    invoker?.Dispose();
                }
            }
        }

        throw lastEx ?? new TimeoutException("WebSocket connection failed across all candidate ports.");
    }
}
