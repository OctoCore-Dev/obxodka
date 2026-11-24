namespace obxodka.Platforms.Windows;

//реализация контракта платформенного драйвера windows для vpn gateway и единого оркестратора
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed partial class WindowsVpnService
{
    public void BindPhysicalSocket(Socket socket) => BindSocketToPhysicalAdapter(socket);

    public Task<bool> PreparePermissionsAndStartServiceAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }

    public async Task CleanupBeforeConnectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var suppressScope = SuppressNetworkChanges();
        Win32WfpLeakGuard.Disable();
        SyncSplitTunnelPolicy?.Invoke();
        await CleanupStaleRoutesAsync().ConfigureAwait(false);
        LogNetworkDiagnostics();
    }

    public async Task PrepareBypassHostsAsync(string[]? ips, CancellationToken ct)
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

    public async Task ApplyTunnelInterfaceAsync(ProtocolRouteStep step, string assignedIpv4, string? assignedIpv6, int mtu,
        CancellationToken ct)
    {
        _ = step;
        _ = mtu;
        using var suppressScope = SuppressNetworkChanges();
        if (_adapter is null || _adapter.IsDisposed)
        {
            _adapter = await Task.Run(() => new WintunAdapter(), ct).ConfigureAwait(false);
        }

        if (!_adapter.IsSessionActive)
        {
            _adapter.StartSession();
        }

        await SetAdapterConfigAsync(_adapter, assignedIpv4, 10, assignedIpv6).ConfigureAwait(false);
    }

    public void StartPacketPump(CancellationToken ct) =>
        _trafficTask = Task.Run(() => ProcessTrafficAsync(ct), CancellationToken.None);

    public async Task ActivateRoutingAndLeakGuardAsync(IReadOnlyList<string> serverIps, string assignedIpv4, string? assignedIpv6,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var adapter = _adapter;
        if (adapter is null)
        {
            return;
        }

        using var suppressScope = SuppressNetworkChanges();
        await SetWindowsRoutesAsync(adapter, serverIps, assignedIpv4, true, assignedIpv6).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await EnableDnsLeakProtectionAsync(adapter, assignedIpv4).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var (tunLuid, _) = ResolveAdapterIdentity(adapter);
        WindowsSplitTunnelEngine.SyncWfpLeakGuard();
        Win32WfpLeakGuard.Enable(tunLuid, serverIps);
        WindowsTimeZoneService.SyncToServerLocation(OctopusEngine.Current.ActiveServerLocation, OctopusEngine.Current.ActiveServerFlag);
    }

    public async Task SyncTunnelIpAndRoutesAsync(IReadOnlyList<string> serverIps, string assignedIpv4, string? assignedIpv6, int mtu,
        CancellationToken ct)
    {
        _ = mtu;
        ct.ThrowIfCancellationRequested();
        var adapter = _adapter;
        if (adapter is null)
        {
            return;
        }

        using var suppressScope = SuppressNetworkChanges();
        await SetAdapterConfigAsync(adapter, assignedIpv4, 10, assignedIpv6).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await SetWindowsRoutesAsync(adapter, serverIps, assignedIpv4, true, assignedIpv6).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await EnableDnsLeakProtectionAsync(adapter, assignedIpv4).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var (tunLuid, _) = ResolveAdapterIdentity(adapter);
        WindowsSplitTunnelEngine.SyncWfpLeakGuard();
        Win32WfpLeakGuard.Enable(tunLuid, serverIps);
        WindowsTimeZoneService.SyncToServerLocation(OctopusEngine.Current.ActiveServerLocation, OctopusEngine.Current.ActiveServerFlag);
    }

    public void UpdateAdapterMtu(int mtu)
    {
        var adapter = _adapter;
        if (adapter is null || adapter.IsDisposed)
        {
            return;
        }

        AppLogger.Log($"[SMART MTU] Optimal MTU updated to {mtu}B. Syncing Wintun adapter...");
        _ = UpdateAdapterMtuAsync(adapter, mtu);
    }

    public async Task HandlePhysicalUplinkLostAsync()
    {
        ResetSavedPhysicalGateway();
        if (!PlatformServices.Preferences.Get(AppPreferenceKeys.KillSwitch, true))
        {
            Win32WfpLeakGuard.Disable();
            await CleanupStaleRoutesAsync().ConfigureAwait(false);
        }
    }

    public async Task ShutdownTunnelAsync(IReadOnlyList<string> serverIps, string assignedIpv4, bool keepKillSwitch)
    {
        if (keepKillSwitch)
        {
            Win32WfpLeakGuard.Enable(0, serverIps);
            return;
        }

        using var suppressScope = SuppressNetworkChanges();
        Win32WfpLeakGuard.Disable();
        WindowsTimeZoneService.RestoreOriginalIfNeeded("отключение VPN");
        var adapter = _adapter;

        await DisableDnsLeakProtectionAsync().ConfigureAwait(false);
        await RestoreAdapterDnsBackupAsync().ConfigureAwait(false);
        await SetWindowsRoutesAsync(adapter, serverIps, assignedIpv4, false).ConfigureAwait(false);

        if (_adapter is not null)
        {
            try
            {
                _adapter.Dispose();
                _adapter = null;
            }
            catch
            {
                // ignored
            }
        }

        if (_trafficTask is not null)
        {
            try
            {
                await _trafficTask.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }

            _trafficTask = null;
        }

        ResetSavedPhysicalGateway();
    }
}
