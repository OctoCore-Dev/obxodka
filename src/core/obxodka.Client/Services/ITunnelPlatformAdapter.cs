namespace obxodka.Services;

//платформенный драйвер виртуального адаптера и сетевых правил ос для оркестратора vpn
public interface ITunnelPlatformAdapter
{
    //поддерживается ли блокировка трафика при обрыве (kill switch)
    bool SupportsKillSwitch { get; }

    //событие смены или появления физической сети со стороны ос (например wifi -> lte на android)
    event Action? OnPlatformNetworkChanged;

    //поиск сторонних vpn клиентов и сетевых конфликтов
    string? DetectConflictingVpn();

    //предварительная проверка сетевой связности на физическом интерфейсе
    Task<string?> RunNetworkPreflightAsync();

    //запрос системных разрешений и запуск нативной службы ос (например vpnservice на android)
    Task<bool> PreparePermissionsAndStartServiceAsync(CancellationToken ct);

    //очистка устаревших маршрутов и бэкапов перед новым подключением
    Task CleanupBeforeConnectAsync(CancellationToken ct);

    //предварительная установка /32 bypass-маршрутов до внешних узлов до открытия сокетов
    Task PrepareBypassHostsAsync(string[] ips, CancellationToken ct);

    //привязка транспортного сокета к физическому интерфейсу (ip_unicast_if / protect fd)
    void BindPhysicalSocket(Socket socket);

    //поднятие и настройка виртуального адаптера (wintun / tun0) перед проверкой дуплекса
    Task ApplyTunnelInterfaceAsync(ProtocolRouteStep step, string assignedIpv4, string? assignedIpv6, int mtu, CancellationToken ct);

    //включение системной маршрутизации и защиты от утечек dns/ipv6 после подтверждения rx > 0
    Task ActivateRoutingAndLeakGuardAsync(IReadOnlyList<string> serverIps, string assignedIpv4, string? assignedIpv6,
        CancellationToken ct);

    //обновление внутреннего ip адреса и маршрутов туннеля при бесшовной ротации протокола
    Task SyncTunnelIpAndRoutesAsync(IReadOnlyList<string> serverIps, string assignedIpv4, string? assignedIpv6, int mtu,
        CancellationToken ct);

    //применение нового mtu на виртуальном интерфейсе ос
    void UpdateAdapterMtu(int mtu);

    //запуск цикла прокачки пакетов между адаптером ос и ядром octopus
    void StartPacketPump(CancellationToken ct);

    //сброс маршрутов туннеля при потере физической сети (без полного закрытия сервиса)
    Task HandlePhysicalUplinkLostAsync();

    //штатное отключение адаптера, восстановление dns/маршрутов и остановка насоса пакетов
    Task ShutdownTunnelAsync(IReadOnlyList<string> serverIps, string assignedIpv4, bool keepKillSwitch);
}
