namespace obxodka.Platforms.Windows;

//реализация контракта платформенного драйвера windows для vpngateway и единого оркестратора
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed partial class WindowsVpnService
{
    public Task<bool> PreparePermissionsAndStartServiceAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }

    public async Task CleanupBeforeConnectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        AppManager.SyncPolicyFromPreferences();
        await CleanupStaleRoutesAsync().ConfigureAwait(false);
        LogNetworkDiagnostics();
    }

    public async Task PrepareBypassHostsAsync(string[] ips, CancellationToken ct)
    {
        if (ips is null || ips.Length == 0)
        {
            return;
        }

        foreach (var ip in ips)
        {
            ct.ThrowIfCancellationRequested();
            if (!IPAddress.TryParse(ip, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
            {
                continue;
            }

            await EnsureHostRouteAsync(ip).ConfigureAwait(false);
        }
    }

    public void BindPhysicalSocket(Socket socket) => BindSocketToPhysicalAdapter(socket);

    public async Task ApplyTunnelInterfaceAsync(ProtocolRouteStep step, string assignedIpv4, string? assignedIpv6, int mtu, CancellationToken ct)
    {
        _ = step;
        _ = mtu;
        if (_adapter is null || _adapter.IsDisposed)
        {
            _adapter = await Task.Run(() => new WintunAdapter("Obxodka", "Obxodka"), ct).ConfigureAwait(false);
        }

        if (!_adapter.IsSessionActive)
        {
            _adapter.StartSession();
        }

        await SetAdapterConfigAsync(_adapter.Name, assignedIpv4, "255.192.0.0", assignedIpv6).ConfigureAwait(false);
    }

    public void StartPacketPump(CancellationToken ct) =>
        _trafficTask = Task.Run(() => ProcessTrafficAsync(ct), CancellationToken.None);

    public async Task ActivateRoutingAndLeakGuardAsync(IReadOnlyList<string> serverIps, string assignedIpv4, string? assignedIpv6, CancellationToken ct)
    {
        _ = ct;
        var adapter = _adapter;
        if (adapter is null)
        {
            return;
        }

        await SetWindowsRoutesAsync(adapter.Name, serverIps, assignedIpv4, true, assignedIpv6).ConfigureAwait(false);
        await EnableDnsLeakProtectionAsync(adapter.Name, assignedIpv4).ConfigureAwait(false);
    }

    public async Task SyncTunnelIpAndRoutesAsync(IReadOnlyList<string> serverIps, string assignedIpv4, string? assignedIpv6, int mtu, CancellationToken ct)
    {
        _ = mtu;
        _ = ct;
        var adapter = _adapter;
        if (adapter is null)
        {
            return;
        }

        await SetAdapterConfigAsync(adapter.Name, assignedIpv4, "255.192.0.0", assignedIpv6).ConfigureAwait(false);
        await SetWindowsRoutesAsync(adapter.Name, serverIps, assignedIpv4, true, assignedIpv6).ConfigureAwait(false);
        await EnableDnsLeakProtectionAsync(adapter.Name, assignedIpv4).ConfigureAwait(false);
    }

    public void UpdateAdapterMtu(int mtu)
    {
        var adapter = _adapter;
        if (adapter is null || adapter.IsDisposed)
        {
            return;
        }

        AppLogger.Log($"[SMART MTU] Optimal MTU updated to {mtu}B. Syncing Wintun adapter...");
        _ = UpdateAdapterMtuAsync(adapter.Name, mtu);
    }

    public async Task HandlePhysicalUplinkLostAsync()
    {
        ResetSavedPhysicalGateway();
        await CleanupStaleRoutesAsync().ConfigureAwait(false);
    }

    public async Task ShutdownTunnelAsync(IReadOnlyList<string> serverIps, string assignedIpv4, bool keepKillSwitch)
    {
        if (keepKillSwitch)
        {
            return;
        }

        var adapterName = _adapter?.Name ?? "Obxodka";

        await DisableDnsLeakProtectionAsync().ConfigureAwait(false);
        await RestoreAdapterDnsBackupAsync().ConfigureAwait(false);
        await SetWindowsRoutesAsync(adapterName, serverIps, assignedIpv4, false).ConfigureAwait(false);

        if (_adapter is not null)
        {
            try
            {
                _adapter.Dispose();
                _adapter = null;
            }
            catch { }
        }

        if (_trafficTask is not null)
        {
            try
            {
                await _trafficTask.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
            catch { }
            _trafficTask = null;
        }

        var deleteHostTasks = new List<Task>();
        foreach (var ip in serverIps)
        {
            if (!string.IsNullOrEmpty(ip) && IPAddress.TryParse(ip, out _))
            {
                deleteHostTasks.Add(RunCmdAsync("route", $"delete {ip} mask 255.255.255.255", timeoutMs: 1500));
            }
        }

        if (deleteHostTasks.Count > 0)
        {
            try
            {
                await Task.WhenAll(deleteHostTasks).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch { }
        }

        ResetSavedPhysicalGateway();
    }
}
