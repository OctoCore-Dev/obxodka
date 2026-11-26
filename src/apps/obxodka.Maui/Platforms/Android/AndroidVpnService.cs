using Android.Net;

namespace obxodka;

//платформенный драйвер android для оркестратора vpn-сессии
[SupportedOSPlatform("android30.0")]
internal sealed class AndroidVpnService : ITunnelPlatformAdapter
{
    private AndroidVpnService()
    {
    }

    public static AndroidVpnService Instance { get; } = new();

    public static VpnSessionOrchestrator Orchestrator { get; } = new(Instance, hookProcessExit: false);

    public bool SupportsKillSwitch => false;

    public event Action? OnPlatformNetworkChanged;

    public string? DetectConflictingVpn() => null;

    public Task<string?> RunNetworkPreflightAsync() =>
        Task.FromResult<string?>(null);

    //запрос системного разрешения vpn service и запуск foreground-службы android
    public async Task<bool> PreparePermissionsAndStartServiceAsync(CancellationToken ct)
    {
        var appContext = Android.App.Application.Context;
        using var intent = VpnService.Prepare(appContext);
        if (intent is not null)
        {
            var granted = await MainActivity.RequestVpnPermissionAsync(intent, ct).ConfigureAwait(false);
            if (!granted)
            {
                return false;
            }
        }

        if (OctopusVpnService.Instance is not { IsActive: true })
        {
            MainActivity.StartVpnService();
        }

        _ = await OctopusVpnService.WaitForServiceReadyAsync(4000, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Не удалось запустить системную службу VPN.");
        return true;
    }

    public Task CleanupBeforeConnectAsync(CancellationToken ct) =>
        Task.CompletedTask;

    public Task PrepareBypassHostsAsync(string[] ips, CancellationToken ct) =>
        Task.CompletedTask;

    //защита исходящего транспортного сокета через VpnService.protect(fd)
    public void BindPhysicalSocket(Socket socket) =>
        _ = OctopusVpnService.ProtectSocket(socket);

    public void UpdateAdapterMtu(int mtu) =>
        OctopusVpnService.Instance?.OnMtuUpdated(mtu);


    public Task HandlePhysicalUplinkLostAsync() =>
        Task.CompletedTask;

    //поднятие виртуального tun0 интерфейса перед проверкой дуплекса канала
    public async Task ApplyTunnelInterfaceAsync(
        ProtocolRouteStep step,
        string assignedIpv4,
        string? assignedIpv6,
        int mtu,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var svc = OctopusVpnService.Instance ?? await OctopusVpnService.WaitForServiceReadyAsync(2000, ct).ConfigureAwait(false);
        var tunOk = svc?.EstablishTun(false, assignedIpv4, assignedIpv6, mtu) ?? false;
        if (!tunOk)
        {
            var err = OctopusVpnService.LastEstablishError ?? "Не удалось инициализировать TUN интерфейс.";
            throw new InvalidOperationException(err);
        }
    }

    public Task ActivateRoutingAndLeakGuardAsync(
        IReadOnlyList<string> serverIps,
        string assignedIpv4,
        string? assignedIpv6,
        CancellationToken ct) =>
        Task.CompletedTask;

    //пересоздание tun0 при смене внутреннего ip адреса во время ротации протокола
    public Task SyncTunnelIpAndRoutesAsync(
        IReadOnlyList<string> serverIps,
        string assignedIpv4,
        string? assignedIpv6,
        int mtu,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _ = OctopusVpnService.Instance?.EstablishTun(false, assignedIpv4, assignedIpv6, mtu);
        return Task.CompletedTask;
    }

    public void StartPacketPump(CancellationToken ct)
    {
        //цикл чтения и записи пакетов tun0 уже запущен внутри EstablishTun
    }

    //остановка нативной службы tun0 на android
    public Task ShutdownTunnelAsync(IReadOnlyList<string> serverIps, string assignedIpv4, bool keepKillSwitch)
    {
        try
        {
            OctopusVpnService.Instance?.StopNativeVpn();
        }
        catch
        {
            // ignored
        }

        return Task.CompletedTask;
    }


    //оповещение оркестратора о смене или появлении физической сети на android
    internal void NotifyPlatformNetworkChanged() =>
        OnPlatformNetworkChanged?.Invoke();
}
