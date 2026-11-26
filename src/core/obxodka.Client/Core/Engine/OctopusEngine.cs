namespace obxodka.Core;

//главный координационный движок vpn клиента obxodka
public sealed partial class OctopusEngine : IDisposable, IAsyncDisposable
{
    private static readonly Lazy<OctopusEngine> s_instance = new(
        () => new OctopusEngine(),
        LazyThreadSafetyMode.ExecutionAndPublication
    );

    //реестр прямых узлов попавших под лимит объема трафика тспу с временным штрафом 30 минут
    private static readonly ConcurrentDictionary<string, long> s_cappedDirectNodes = new(StringComparer.OrdinalIgnoreCase);

    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "Синглтон-блокировка ротации живет весь жизненный цикл")]
    private readonly SemaphoreSlim _rotationGate = new(1, 1);

    private volatile uint _assignedIpUint = BinaryPrimitives.ReadUInt32BigEndian([10, 8, 0, 2]);

    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "Потокобезопасное освобождение выполняется через Interlocked.Exchange")]
    private X509Certificate2? _clientCert;

    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "Потокобезопасное освобождение выполняется через Interlocked.Exchange")]
    private CancellationTokenSource? _cts;

    private int _isEscalatingToShield;
    private long _largeRxPackets;

    private long _largeTxPackets;
    private long _lastHealthyRxTicks;
    private Action<Socket>? _protectAction;
    private List<ProtocolRouteStep> _rotationRing = [];
    private int _rotationStepIndex;
    private double _smoothedPing;
    private long _totalBytesReceived;

    private long _totalBytesSent;

    [SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance")]
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "Потокобезопасное освобождение выполняется через Interlocked.Exchange")]
    private IVpnTransport? _transport;

    private OctopusEngine()
    {
        Pipeline = new PacketPipeline(this)
        {
            LocalDelivery = DeliverLocalPacket
        };
        EnsureUplinkMonitoringInitialized();
    }

    //одиночный экземпляр движка
    public static OctopusEngine Current => s_instance.Value;

    //сквозной конвейер обработки сетевых пакетов (ssot pipeline)
    public PacketPipeline Pipeline { get; }

    //динамический хэш открытого ключа ssl pinning
    public static string? DynamicSslPublicKeyHash { get; set; }

    //постоянный хэш сертификата прямого origin-узла (не затирается при работе через shield)
    public static string? DirectOriginCertHash { get; set; }

    //обратный вызов платформы для регистрации /32 bypass-маршрутов при переключении внешних ip по кругу
    public Func<string[], CancellationToken, Task>? EnsureExternalHostRoutesAsync { get; set; }

    //число активных лучей туннеля
    public int ActiveRays
    {
        get => (_transport as ObxodkaStreamTransport)?.ActiveRayCount ?? field;
        private set;
    } = 2;

    //флаг активности соединения
    public bool IsConnected => _transport is { IsConnected: true };

    //выделенный сервером ipv4 адрес клиента
    public string AssignedIp
    {
        get;
        private set
        {
            field = value;
            _assignedIpUint = IPAddress.TryParse(value, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork
                ? BinaryPrimitives.ReadUInt32BigEndian(parsed.GetAddressBytes())
                : 0u;
        }
    } = "10.8.0.2";

    //ipv4 адрес клиента в сетевом порядке байт для быстрой проверки источника пакетов в конвейере
    internal uint AssignedIpUint => _assignedIpUint;

    //выделенный сервером ipv6 адрес клиента
    public string AssignedIpV6 { get; private set; } = "fd00::2";

    //всего отправлено байт через туннель
    public long TotalBytesSent => Interlocked.Read(ref _totalBytesSent);

    //всего принято байт через туннель
    public long TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);

    //название текущего активного протокола
    public string ActiveProtocol { get; private set; } = "AUTO";

    //человекочитаемое название текущего активного протокола
    public string ActiveProtocolDisplayName { get; private set; } = "Obxodka";

    //активное имя sni маскировки
    public string ActiveSni => (_transport as ObxodkaStreamTransport)?.ActiveSni ??
                               (_transport as ObxodkaRealityTransport)?.ActiveSni ??
                               (_transport as ObxodkaWebSocketTransport)?.ActiveSni ?? string.Empty;

    //активный браузерный профиль маскировки tls (ssot)
    public TlsBrowserProfile ActiveBrowserProfile =>
        (_transport as ObxodkaStreamTransport)?.ActiveBrowserProfile ??
        (_transport as ObxodkaWebSocketTransport)?.ActiveBrowserProfile ??
        TlsBrowserProfile.Chrome;

    //активный ip-адрес подключенного сервера (ssot)
    public string? ConnectedServerIp { get; private set; }

    //активный порт подключенного сервера (ssot)
    public int ConnectedServerPort { get; private set; }

    //активная локация подключенного сервера для синхронизации часового пояса и ui (ssot)
    public string? ActiveServerLocation { get; private set; }

    //эмодзи флага активного сервера (ssot)
    public string? ActiveServerFlag { get; private set; }

    //обновление метаданных локации активного сервера из оркестратора
    public void SetActiveServerLocation(string? location, string? flag = null)
    {
        ActiveServerLocation = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
        ActiveServerFlag = string.IsNullOrWhiteSpace(flag) ? null : flag.Trim();
    }

    //единая сборка строки хендшейка с криптографической подписью ecdsa p-256 сертификата устройства и опциональным бюджетом hydra
    public string BuildAuthenticatedHandshakeString(
        string thumbprint,
        string? assignedIp,
        int ray,
        int mtu,
        uint sessionSeed,
        long timestampUnixSeconds,
        bool includePublicKey = false,
        int hydraSocketBudgetBytes = 0)
    {
        var proof = ObxodkaFraming.ComputeHandshakeProof(thumbprint, ray, sessionSeed, timestampUnixSeconds);
        var core = string.IsNullOrEmpty(assignedIp)
            ? $"{thumbprint}|R:{ray}|M:{mtu}|K:{sessionSeed:X8}|T:{timestampUnixSeconds}|P:{proof}"
            : $"{thumbprint}|{assignedIp}|R:{ray}|M:{mtu}|K:{sessionSeed:X8}|T:{timestampUnixSeconds}|P:{proof}";
        if (hydraSocketBudgetBytes > 0)
        {
            core = $"{core}|H:{hydraSocketBudgetBytes}";
        }

        var cert = Volatile.Read(ref _clientCert);
        if (cert is null)
        {
            return core;
        }

        try
        {
            using var ecdsa = cert.GetECDsaPrivateKey();
            if (ecdsa is null)
            {
                return core;
            }

            var sig = ObxodkaFraming.SignHandshakeEcdsa(ecdsa, thumbprint, ray, sessionSeed, timestampUnixSeconds);
            if (string.IsNullOrEmpty(sig))
            {
                return core;
            }

            if (!includePublicKey)
            {
                return $"{core}|S:{sig}";
            }

            var spki = System.Buffers.Text.Base64Url.EncodeToString(ecdsa.ExportSubjectPublicKeyInfo());
            return $"{core}|S:{sig}|PK:{spki}";
        }
        catch (Exception ex) when (ex is CryptographicException or ObjectDisposedException)
        {
            return core;
        }
    }

    //активный целевой sni для маскировки tls
    private string? ActiveTargetSni { get; set; }

    //пометка прямого узла как заблокированного по порогу трафика тспу
    public static void MarkNodeByteCapped(string? serverIp)
    {
        if (string.IsNullOrWhiteSpace(serverIp) || !IPAddress.TryParse(serverIp, out var ip) || IPAddress.IsLoopback(ip))
        {
            return;
        }

        s_cappedDirectNodes[serverIp] = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 1800;
    }

    //проверка действует ли штраф лимита трафика тспу для прямого узла
    public static bool IsNodeByteCapped(string? serverIp)
    {
        if (string.IsNullOrWhiteSpace(serverIp) || !s_cappedDirectNodes.TryGetValue(serverIp, out var expiresAt))
        {
            return false;
        }

        if (Stopwatch.GetTimestamp() < expiresAt)
        {
            return true;
        }

        _ = s_cappedDirectNodes.TryRemove(serverIp, out _);
        return false;
    }

    //сброс штрафов прямых узлов при смене физической сети
    public static void ClearByteCappedNodes() => s_cappedDirectNodes.Clear();

    //событие смены внутреннего ip туннеля при бесшовной ротации протокола
    public event Action? OnTunnelIpChanged;

    //событие смены активного транспортного протокола
    public event Action? OnProtocolChanged;

    //событие приема сетевого пакета из туннеля
    public event Action<byte[], int>? OnPacketReceived;
    private event Action<byte[], int>? OnProbePacketInspect;

    //событие обрыва соединения
    public event Action? OnConnectionDropped;

    //событие обнаружения зависшего блэкхол соединения
    public event Action? OnDeadConnectionDetected;

    //событие обновления статистики трафика
    public event Action<long, long>? OnTrafficUpdated;

    //событие обновления пинга
    public event Action<long>? OnPingUpdated;

    //событие отправки текстового статуса в ui
    public event Action<string>? OnStatusMessage;

    //событие изменения согласованного mtu
    public event Action<int>? OnMtuUpdated;

    //событие отзыва или устаревания клиентского сертификата
    public static event Action<string>? OnCertificateRevoked;

    //отправка статусного сообщения в ui и логгер
    public void EmitStatus(string message)
    {
        OnStatusMessage?.Invoke(message);
        AppLogger.Log(message);
    }

    //сброс счетчиков трафика и состояния монитора
    public void ResetTrafficCounters()
    {
        _ = Interlocked.Exchange(ref _totalBytesSent, 0);
        _ = Interlocked.Exchange(ref _totalBytesReceived, 0);
        _ = Interlocked.Exchange(ref _largeTxPackets, 0);
        _ = Interlocked.Exchange(ref _largeRxPackets, 0);
        Volatile.Write(ref _watchdogArmedTicks, 0);
        Volatile.Write(ref _lastHealthyRxTicks, Stopwatch.GetTimestamp());
        _smoothedPing = 0;
    }

    //обработка и сглаживание замера rtt пинга через фильтр экспоненциального скользящего среднего
    private void ReportPing(long rawRtt)
    {
        if (rawRtt < 0)
        {
            return;
        }

        Volatile.Write(ref _lastHealthyRxTicks, Stopwatch.GetTimestamp());
        var effectiveRtt = Math.Max(1L, rawRtt);
        if (_smoothedPing <= 0)
        {
            _smoothedPing = effectiveRtt;
        }
        else if (effectiveRtt < _smoothedPing)
        {
            var alpha = 0.55;
            _smoothedPing = _smoothedPing * (1 - alpha) + effectiveRtt * alpha;
        }
        else
        {
            var alpha = effectiveRtt > _smoothedPing * 1.5 ? 0.05 : 0.20;
            _smoothedPing = _smoothedPing * (1 - alpha) + effectiveRtt * alpha;
        }

        OnPingUpdated?.Invoke((long)Math.Round(_smoothedPing));
    }

    //отправка синтезированного управляющего l3 пакета (icmp pmtud) обратно в локальный tun адаптер ос
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DeliverLocalPacket(byte[] packet, int length) =>
        OnPacketReceived?.Invoke(packet, length);

    //учет реально принятого в туннель исходящего пакета для статистики и сторожа застоя
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RecordOutgoingPacketBytes(int length)
    {
        _ = Interlocked.Add(ref _totalBytesSent, length);
        if (length > SmartMtuEngine.DefaultMtu)
        {
            _ = Interlocked.Increment(ref _largeTxPackets);
        }
    }

    //отправка пакета напрямую из пула памяти через центральный конвейер обработки
    public void SendPacketFromPool(byte[] inputBuf, int length) =>
        Pipeline.ProcessOutgoingPacket(inputBuf, length, _transport);

    //асинхронная обертка отправки пакета из пула для совместимости
    public Task SendPacketFromPoolAsync(byte[] inputBuf, int length)
    {
        SendPacketFromPool(inputBuf, length);
        return Task.CompletedTask;
    }

    //асинхронная отправка пакета в туннель из массива
    private Task SendPacketAsync(byte[] packet)
    {
        if (!IsConnected || _transport is null || packet.Length < 20 || IsServerDestination(packet.AsSpan(0, packet.Length), packet.Length))
        {
            return Task.CompletedTask;
        }

        var poolBuf = ArrayPool<byte>.Shared.Rent(packet.Length);
        Buffer.BlockCopy(packet, 0, poolBuf, 0, packet.Length);
        return SendPacketFromPoolAsync(poolBuf, packet.Length);
    }
}
