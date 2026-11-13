namespace obxodka.Core.Transports;

//транспорт туннеля поверх защищенного websocket соединения
public sealed partial class ObxodkaWebSocketTransport(int serverPort = 443, string? configuredSni = null, string? preferredAssignedIp = null) : IVpnTransport
{
    //элемент очереди исходящих фреймов websocket
    private readonly struct WsTxItem(byte command, byte[] buffer, int length)
    {
        public readonly byte Command = command;
        public readonly byte[] Buffer = buffer;
        public readonly int Length = length;
    }

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

    //очередь исходящих пакетов для строго однопоточной отправки
    private readonly Channel<WsTxItem> _txChannel =
        Channel.CreateBounded<WsTxItem>(new BoundedChannelOptions(16384)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

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
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
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
                            await socket.ConnectAsync(new IPEndPoint(candidateIps[ipIdx], port), perIpCts.Token).ConfigureAwait(false);
                        }
                        else
                        {
                            await socket.ConnectAsync(new IPEndPoint(candidateIps[ipIdx], port), cToken).ConfigureAwait(false);
                        }

                        connectedSocket = socket;
                        break;
                    }
                    catch (Exception ex)
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
                var isShield = SniEngine.IsShieldEndpoint(serverIp, targetSni);
                return (!isShield && isDirectIp && literalIp is not null && !IPAddress.IsLoopback(literalIp))
                    ? DpiBypassStream.CreateForProfile(netStream, browserProfile, bypassEnabled: true)
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

            //для резервных браузерных профилей выполняем быструю 1-rtt проверку проходимости raw clienthello через тспу
            if (pIdx > 0 && pIdx + 1 < candidates.Count && IPAddress.TryParse(candidateServerIp, out var probeIp) && !IPAddress.IsLoopback(probeIp))
            {
                var rawReachable = await RoutingHello.ProbeRawClientHelloAsync(
                    new IPEndPoint(probeIp, candidatePort),
                    candidateSni,
                    ActiveBrowserProfile,
                    socket =>
                    {
                        OnSocketCreated?.Invoke(socket);
                        ObxodkaStreamTransport.OnSocketCreated?.Invoke(socket);
                        _protectAction?.Invoke(socket);
                    },
                    timeoutMs: 900,
                    ct: ct).ConfigureAwait(false);

                if (!rawReachable)
                {
                    AppLogger.LogWarning($"[BROWSER-ROTATION] 1-RTT Raw ClientHello probe blocked for '{candidateSni}:{candidatePort}' ({ActiveBrowserProfile}). Rotating to next browser profile...");
                    continue;
                }
            }

            _cts = new CancellationTokenSource();
            _ipTcs = new TaskCompletionSource<(string ip, string ip6)>(TaskCreationOptions.RunContinuationsAsynchronously);

            var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            ws.Options.SetBuffer(131072, 131072);
            try
            {
                ws.Options.SetRequestHeader("Host", targetHostHeader);
            }
            catch { }
            RoutingHello.ApplyWebSocketHeaders(ws.Options, ActiveBrowserProfile);

            var cid = RandomNumberGenerator.GetInt32(10000000, 100000000);
            var nowQuerySec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var uriBuilder = new UriBuilder("wss", connectHost, candidatePort, wsPath)
            {
                Query = $"cid={cid}&_t={nowQuerySec}&v=133.0"
            };

            var invoker = CreateWsInvoker(candidateSni, candidateServerIp, candidatePort);
            using var attemptCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(8000));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token, attemptCts.Token);

            try
            {
                AppLogger.Log($"[OBXODKA-WS] Connecting to {uriBuilder.Uri} (SNI={ActiveSni}, Port={candidatePort}, Profile={ActiveBrowserProfile})...");
                await ws.ConnectAsync(uriBuilder.Uri, invoker, linkedCts.Token).ConfigureAwait(false);

                _wsInvoker?.Dispose();
                _wsInvoker = invoker;
                _webSocket = ws;
                Volatile.Write(ref _connected, true);

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

                _ = Task.Run(() => ReaderLoopAsync(ws, _cts.Token), _cts.Token);
                _ = Task.Run(() => WriterLoopAsync(ws, _cts.Token), _cts.Token);
                _ = Task.Run(() => PingLoopAsync(_cts.Token), _cts.Token);

                using (linkedCts.Token.Register(() => _ipTcs.TrySetCanceled()))
                {
                    var result = await _ipTcs.Task.ConfigureAwait(false);
                    _ = SendPingProbeAsync();
                    AppLogger.Log($"[OBXODKA-WS] Connected on port {candidatePort} (Browser: {ActiveBrowserProfile})! Assigned IPv4: {result.ip}, IPv6: {result.ip6}");
                    return result;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && pIdx + 1 < candidates.Count)
            {
                lastEx = ex;
                AppLogger.LogWarning($"[OBXODKA-WS] Port {candidatePort} (Profile {ActiveBrowserProfile}) failed: {ex.Message}. Trying next candidate...");
                try
                {
                    ws.Dispose();
                }
                catch { }
                try
                {
                    invoker.Dispose();
                }
                catch { }
            }
        }

        throw lastEx ?? new TimeoutException("WebSocket connection failed across all candidate ports.");
    }
}
