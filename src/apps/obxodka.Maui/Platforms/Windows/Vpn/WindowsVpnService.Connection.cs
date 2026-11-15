using Uri = System.Uri;

namespace obxodka.Platforms.Windows;

//управление сетевым подключением vpn, интеграция с ядром octopus и обработка failover
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed partial class WindowsVpnService
{
    //обработка обновления mtu ядра octopus
    private void HandleMtuUpdated(int optimalMtu)
    {
        AppLogger.Log($"[SMART MTU] Optimal MTU updated to {optimalMtu}B. Syncing Wintun adapter...");
        if (_adapter is not null)
        {
            _ = UpdateAdapterMtuAsync(_adapter.Name, optimalMtu);
        }
    }

    //обработка смены внутреннего ip адреса туннеля при бесшовной ротации протокола
    private void HandleTunnelIpChanged()
    {
        if (_adapter is null || _isExplicitlyStopped)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await SyncAdapterAssignedIpAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLogger.LogWarning($"[WINDOWS-VPN] Tunnel IP sync after rotation failed: {ex.Message}");
            }
        });
    }

    //пакетная регистрация /32 bypass-маршрутов для активного узла кругового кольца до открытия сокетов
    private async Task EnsureHostRoutesBatchAsync(string[] ips, CancellationToken ct)
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

            if (!_currentServerIpsToRoute.Contains(ip))
            {
                _currentServerIpsToRoute.Add(ip);
            }

            await EnsureHostRouteAsync(ip).ConfigureAwait(false);
        }
    }

    //синхронизация назначенного ip адреса туннеля с адаптером wintun
    private async Task SyncAdapterAssignedIpAsync()
    {
        var newAssignedIp = OctopusEngine.Current.AssignedIp;
        if (_adapter is null || string.IsNullOrEmpty(newAssignedIp) || !IPAddress.TryParse(newAssignedIp, out _))
        {
            return;
        }

        if (!string.Equals(_currentAssignedIp, newAssignedIp, StringComparison.Ordinal))
        {
            AppLogger.Log($"[WINDOWS-VPN] Assigned tunnel IP changed ({_currentAssignedIp} -> {newAssignedIp}). Updating Wintun adapter and routes...");
            _currentAssignedIp = newAssignedIp;
            var ipv6 = OctopusEngine.Current.AssignedIpV6;
            var hasValidIpv6 = !string.IsNullOrWhiteSpace(ipv6) && IPAddress.TryParse(ipv6, out var parsedV6) && parsedV6.AddressFamily == AddressFamily.InterNetworkV6;
            var effectiveIpv6 = hasValidIpv6 ? ipv6 : null;
            await SetAdapterConfigAsync(_adapter.Name, newAssignedIp, "255.192.0.0", effectiveIpv6).ConfigureAwait(false);
            await SetWindowsRoutesAsync(_adapter.Name, _currentServerIpsToRoute, newAssignedIp, true, effectiveIpv6).ConfigureAwait(false);
            await EnableDnsLeakProtectionAsync(_adapter.Name, newAssignedIp).ConfigureAwait(false);
        }
        else
        {
            _ = UpdateAdapterMtuAsync(_adapter.Name, SmartMtuEngine.CurrentMtu);
        }
    }

    //обработка потери физической сети: снятие маршрутов wintun для исключения блэкхола интернета
    private void HandleUplinkLost()
    {
        if (!IsRunning || _isExplicitlyStopped)
        {
            return;
        }

        AppLogger.LogWarning("[WINDOWS-VPN] Physical uplink lost during active VPN. Removing Wintun routes to avoid blackhole...");
        OnLogUpdated?.Invoke("Физическая сеть отключена. Маршруты сброшены.");
        ResetSavedPhysicalGateway();
        UpdateState(AppVpnState.Reconnecting);
        _ = Task.Run(CleanupStaleRoutesAsync);
    }

    //обработка восстановления физической сети: автоматическое переподключение
    private void HandleUplinkRestored()
    {
        if (CurrentState != AppVpnState.Reconnecting || _isExplicitlyStopped)
        {
            return;
        }

        AppLogger.Log("[WINDOWS-VPN] Physical uplink restored. Re-establishing connection...");
        OnLogUpdated?.Invoke("Сеть восстановлена. Подключение к узлу...");
        _ = Task.Run(async () =>
        {
            if (Preferences.Get(AppPreferenceKeys.AutoReconnect, true))
            {
                var targetPort = OctopusEngine.Current.ConnectedServerPort > 0 ? OctopusEngine.Current.ConnectedServerPort : 443;
                await StartVpnAsync(_currentServerIp, targetPort, _fallbackServers).ConfigureAwait(false);
            }
            else
            {
                await StopVpnAsync().ConfigureAwait(false);
            }
        });
    }

    //обработка потери связи при отсутствии ответа от сервера
    private void HandleDeadConnection()
    {
        if (!IsRunning || _isExplicitlyStopped)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _isHandlingDeadConnection, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                //если физическая сеть отключена — не крутим циклы перебора серверов
                if (!OctopusEngine.IsUplinkAvailable)
                {
                    AppLogger.LogWarning("[DEAD CONNECTION] Physical network is down. Dropping tunnel routes without fallback loops.");
                    OnLogUpdated?.Invoke("Физическая сеть отключена. Ожидание подключения...");
                    UpdateState(AppVpnState.Reconnecting);
                    await CleanupStaleRoutesAsync().ConfigureAwait(false);
                    return;
                }
                AppLogger.LogWarning($"[DEAD CONNECTION] Packet blackhole detected on Windows. Initiating circular protocol recovery from {_currentServerIp}:{OctopusEngine.Current.ConnectedServerPort}...");
                OnLogUpdated?.Invoke(VpnStatusMessages.PacketLossDetected);
                UpdateState(AppVpnState.Reconnecting);

                try
                {
                    if (await OctopusEngine.Current.TryRotateProtocolAsync("DeadConnectionRecovery").ConfigureAwait(false))
                    {
                        if (_adapter is not null)
                        {
                            await SyncAdapterAssignedIpAsync().ConfigureAwait(false);
                        }
                        OctopusEngine.Current.ResetTrafficCounters();
                        OctopusEngine.Current.ArmTrafficWatchdog();
                        UpdateState(AppVpnState.Connected);
                        OnLogUpdated?.Invoke(VpnStatusMessages.ProtocolSwitched(OctopusEngine.Current.ActiveProtocolDisplayName));
                        return;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    AppLogger.LogWarning($"[DEAD CONNECTION] In-place protocol rotation failed: {ex.Message}");
                }

                for (var attempt = 1; attempt <= 2; attempt++)
                {
                    if (_isExplicitlyStopped || !OctopusEngine.IsUplinkAvailable)
                    {
                        return;
                    }

                    try
                    {
                        await OctopusEngine.Current.ReconnectAsync().ConfigureAwait(false);
                        var activeIp = OctopusEngine.Current.ConnectedServerIp;
                        if (IPAddress.TryParse(activeIp, out _))
                        {
                            _currentServerIp = activeIp;
                        }

                        var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(5000), forceFreshProbe: true).ConfigureAwait(false);
                        if (verified)
                        {
                            if (_adapter is not null)
                            {
                                await SyncAdapterAssignedIpAsync().ConfigureAwait(false);
                            }
                            OctopusEngine.Current.ResetTrafficCounters();
                            OctopusEngine.Current.ArmTrafficWatchdog();
                            UpdateState(AppVpnState.Connected);
                            OnLogUpdated?.Invoke(VpnStatusMessages.SwitchedToBackupNodeSuccess);
                            return;
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        AppLogger.LogWarning($"[SMART CONNECT] Circular reconnect attempt {attempt} failed: {ex.Message}");
                    }
                }

                if (_isExplicitlyStopped)
                {
                    return;
                }

                await StopVpnAsync();
                UpdateState(AppVpnState.Error);
                OnErrorOccurred?.Invoke("Ни один из протоколов не получил ответного трафика (0 RX).");
            }
            finally
            {
                Volatile.Write(ref _isHandlingDeadConnection, 0);
            }
        });
    }

    //обработка обрыва сетевого соединения движка
    private void HandleEngineDrop()
    {
        var autoReconnect = Preferences.Get(AppPreferenceKeys.AutoReconnect, true);
        var killSwitch = Preferences.Get(AppPreferenceKeys.KillSwitch, false);

        if (IsRunning && !_isExplicitlyStopped)
        {
            if (!autoReconnect)
            {
                _ = Task.Run(async () =>
                {
                    await StopVpnAsync();
                    UpdateState(AppVpnState.Error);
                    OnErrorOccurred?.Invoke("Связь с сервером потеряна.");
                });
                return;
            }

            UpdateState(AppVpnState.Reconnecting);
            _ = Task.Run(async () =>
            {
                if (Interlocked.CompareExchange(ref _isHandlingDrop, 1, 0) != 0)
                {
                    return;
                }

                try
                {
                    if (!OctopusEngine.IsUplinkAvailable)
                    {
                        AppLogger.LogWarning("[ENGINE DROP] Physical network is down. Dropping routes without retry loops.");
                        OnLogUpdated?.Invoke("Физическая сеть отключена. Ожидание подключения...");
                        UpdateState(AppVpnState.Reconnecting);
                        await CleanupStaleRoutesAsync().ConfigureAwait(false);
                        return;
                    }

                    for (var i = 0; i < 4; i++)
                    {
                        await Task.Delay(1200 + (i * 800)).ConfigureAwait(false);
                        if (_isExplicitlyStopped || !OctopusEngine.IsUplinkAvailable)
                        {
                            return;
                        }

                        try
                        {
                            await OctopusEngine.Current.ReconnectAsync().ConfigureAwait(false);
                            if (_isExplicitlyStopped)
                            {
                                return;
                            }

                            var activeIp = OctopusEngine.Current.ConnectedServerIp;
                            if (IPAddress.TryParse(activeIp, out _))
                            {
                                _currentServerIp = activeIp;
                            }

                            var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(4500), forceFreshProbe: true).ConfigureAwait(false);
                            if (_isExplicitlyStopped)
                            {
                                return;
                            }

                            if (verified)
                            {
                                if (_adapter is not null)
                                {
                                    await SyncAdapterAssignedIpAsync().ConfigureAwait(false);
                                }
                                OctopusEngine.Current.ResetTrafficCounters();
                                OctopusEngine.Current.ArmTrafficWatchdog();
                                UpdateState(AppVpnState.Connected);
                                OnLogUpdated?.Invoke(VpnStatusMessages.ConnectionRestored);
                                return;
                            }

                            await OctopusEngine.Current.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            AppLogger.LogWarning($"[RECONNECT] Circular drop recovery attempt {i + 1} failed: {ex.Message}");
                            await OctopusEngine.Current.DisposeAsync().ConfigureAwait(false);
                        }
                    }

                    if (killSwitch)
                    {
                        OnErrorOccurred?.Invoke("Не удалось восстановить связь. Kill Switch блокирует утечку IP. Нажмите «Стоп» для отключения.");
                    }
                    else
                    {
                        await StopVpnAsync();
                        OnErrorOccurred?.Invoke("Связь с сервером потеряна. Не удалось восстановить подключение.");
                    }
                }
                finally
                {
                    Volatile.Write(ref _isHandlingDrop, 0);
                }
            });
        }
    }

    //перегрузка запуска vpn с одним сервером
    public Task StartVpnAsync(string serverIp, int serverPort) =>
        StartVpnAsync(serverIp, serverPort, null);

    //запуск vpn сессии со списком резервных узлов
    public async Task StartVpnAsync(string serverIp, int serverPort, IReadOnlyList<VpnServerDto>? fallbackServers)
    {
        await _vpnGate.WaitAsync();
        try
        {
            _fallbackServers = fallbackServers != null ? [.. fallbackServers] : [];
            _currentServerIndex = _fallbackServers.FindIndex(s => s.Ip == serverIp);
            if (_currentServerIndex < 0 && !string.IsNullOrEmpty(serverIp))
            {
                _fallbackServers.Insert(0, new VpnServerDto(serverIp, serverPort, "", true, 0, null));
                _currentServerIndex = 0;
            }

            UpdateState(AppVpnState.Connecting);
            OnLogUpdated?.Invoke(VpnStatusMessages.CleaningOldSettings);
            await CleanupStaleRoutesAsync();

            var targetIp = serverIp;
            if (Uri.CheckHostName(serverIp) == UriHostNameType.Dns)
            {
                try
                {
                    var ips = new[] { await ApiService.ResolveHostAntiPoisonAsync(serverIp).ConfigureAwait(false) };
                    if (ips.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) is { } ipv4)
                    {
                        targetIp = ipv4.ToString();
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"[DNS ERROR] Could not resolve {serverIp}: {ex.Message}");
                    await AutoHealAdapterDnsAsync().ConfigureAwait(false);
                }
            }

            _currentServerIp = targetIp;
            _isExplicitlyStopped = false;

            try
            {
                LogNetworkDiagnostics(OnLogUpdated);
                OnLogUpdated?.Invoke(VpnStatusMessages.BuildingRoute(targetIp));

                var serversToTry = _fallbackServers.Count > 0
                    ? _fallbackServers
                    : [new VpnServerDto(serverIp, serverPort, "", true, 0, null)];

                var forceWebSocket = Preferences.Get(AppPreferenceKeys.ForceWebSocket, false);
                var ring = SniEngine.BuildProtocolRotationRing(serversToTry, serverIp, serverPort, configuredSni: null, forceWebSocket);
                if (ring.Count == 0)
                {
                    throw new InvalidOperationException("Нет доступных маршрутов или протоколов для подключения.");
                }

                _currentServerIpsToRoute.Clear();
                OctopusEngine.Current.EnsureExternalHostRoutesAsync = EnsureHostRoutesBatchAsync;

                if (_cts is not null)
                {
                    await _cts.CancelAsync().ConfigureAwait(false);
                    _cts.Dispose();
                }
                _cts = new CancellationTokenSource();

                string? activeIpv6 = null;
                var activeStep = await OctopusEngine.Current.ConnectRotationRingAsync(
                    ring,
                    prepareTunnelBeforeVerifyAsync: async (step, stepToken) =>
                    {
                        stepToken.ThrowIfCancellationRequested();
                        if (_isExplicitlyStopped)
                        {
                            throw new OperationCanceledException("Stopped by user.");
                        }

                        var resolvedStepIp = IPAddress.TryParse(step.Host, out _)
                            ? step.Host
                            : (_currentServerIpsToRoute.FirstOrDefault() ?? targetIp);
                        _currentServerIp = resolvedStepIp;
                        OnLogUpdated?.Invoke(VpnStatusMessages.ConnectingWithProtocol(step.Host, step.Port, OctopusEngine.Current.ActiveProtocolDisplayName));

                        var ip = OctopusEngine.Current.AssignedIp;
                        var ipv6 = OctopusEngine.Current.AssignedIpV6;
                        if (!IPAddress.TryParse(ip, out _))
                        {
                            throw new InvalidOperationException("Получен некорректный IP-адрес от сервера.");
                        }

                        var hasValidIpv6 = !string.IsNullOrWhiteSpace(ipv6) && IPAddress.TryParse(ipv6, out var parsedV6) && parsedV6.AddressFamily == AddressFamily.InterNetworkV6;
                        activeIpv6 = hasValidIpv6 ? ipv6 : null;

                        OnLogUpdated?.Invoke(VpnStatusMessages.IpAssigned(ip));
                        if (_adapter is null || _adapter.IsDisposed)
                        {
                            OnLogUpdated?.Invoke(VpnStatusMessages.InitializingWintunAdapter);
                            _adapter = await Task.Run(() => new WintunAdapter("Obxodka", "Obxodka"), stepToken).ConfigureAwait(false);
                        }

                        if (!_adapter.IsSessionActive)
                        {
                            OnLogUpdated?.Invoke(VpnStatusMessages.StartingAdapter(_adapter.Name));
                            _adapter.StartSession();
                        }

                        OnLogUpdated?.Invoke(VpnStatusMessages.ApplyingNetworkSettings);
                        _currentAssignedIp = ip;
                        await SetAdapterConfigAsync(_adapter.Name, ip, "255.192.0.0", activeIpv6).ConfigureAwait(false);
                        OnLogUpdated?.Invoke(VpnStatusMessages.CheckingChannelDuplex);
                    },
                    ct: _cts.Token).ConfigureAwait(false);

                if (_isExplicitlyStopped || _adapter is null)
                {
                    UpdateState(AppVpnState.Disconnected);
                    return;
                }

                var curTx = OctopusEngine.Current.TotalBytesSent;
                var curRx = OctopusEngine.Current.TotalBytesReceived;
                AppLogger.Log($"[WINDOWS-VPN] Circular ring connected on {activeStep.Kind} ({activeStep.Host}:{activeStep.Port}), TX={curTx} B, RX={curRx} B, MTU={SmartMtuEngine.CurrentMtu}");
                OnLogUpdated?.Invoke(VpnStatusMessages.ChannelVerifiedDuplex(curTx, curRx));

                _trafficTask = Task.Run(() => ProcessTrafficAsync(_cts.Token));

                OnLogUpdated?.Invoke(VpnStatusMessages.RedirectingTraffic);
                await SetWindowsRoutesAsync(_adapter.Name, _currentServerIpsToRoute, _currentAssignedIp, true, activeIpv6).ConfigureAwait(false);
                OnLogUpdated?.Invoke(VpnStatusMessages.EnablingDnsProtection);
                await EnableDnsLeakProtectionAsync(_adapter.Name, _currentAssignedIp).ConfigureAwait(false);

                OctopusEngine.Current.ResetTrafficCounters();
                OctopusEngine.Current.ArmTrafficWatchdog();

                OnLogUpdated?.Invoke(VpnStatusMessages.ProtectedConnectionActive);
                UpdateState(AppVpnState.Connected);
            }
            catch (OperationCanceledException) when (_isExplicitlyStopped)
            {
                UpdateState(AppVpnState.Disconnected);
            }
            catch (Exception ex)
            {
                if (_cts is not null)
                {
                    await _cts.CancelAsync().ConfigureAwait(false);
                }

                try
                {
                    _adapter?.Dispose();
                    _adapter = null;
                }
                catch { }

                await OctopusEngine.Current.DisposeAsync().ConfigureAwait(false);

                if (_isExplicitlyStopped)
                {
                    UpdateState(AppVpnState.Disconnected);
                    return;
                }

                AppLogger.LogError($"[WINDOWS-VPN FATAL] Connection pipeline failed completely: {ex.Message}", ex);
                UpdateState(AppVpnState.Error);
                var errMsg = ex is TimeoutException || ex.InnerException is TimeoutException
                    ? ex.Message
                    : $"Ошибка: {ex.Message}";
                OnLogUpdated?.Invoke(errMsg);
                OnErrorOccurred?.Invoke(errMsg);
            }
        }
        finally
        {
            _ = _vpnGate.Release();
        }
    }

    //остановка vpn сессии и очистка интерфейсов
    public async Task StopVpnAsync()
    {
        _isExplicitlyStopped = true;

        if (CurrentState == AppVpnState.Disconnected && (_adapter == null || !_adapter.IsSessionActive))
        {
            await OctopusEngine.Current.DisposeAsync().ConfigureAwait(false);
            return;
        }

        UpdateState(AppVpnState.Disconnecting);

        var lockTaken = false;
        try
        {
            lockTaken = await _vpnGate.WaitAsync(5000).ConfigureAwait(false);
            if (!lockTaken)
            {
                AppLogger.LogWarning("[WARN] StopVpnAsync timed out waiting for gate lock.");
            }

            OnLogUpdated?.Invoke(VpnStatusMessages.DisconnectingVpn);

            try
            {
                if (_cts != null)
                {
                    await _cts.CancelAsync().ConfigureAwait(false);
                }

                var adapterName = _adapter?.Name ?? "Obxodka";
                List<string> ipsToRoute = _currentServerIpsToRoute.Count > 0
                    ? [.. _currentServerIpsToRoute]
                    : (!string.IsNullOrEmpty(_currentServerIp) ? [_currentServerIp] : []);

                await DisableDnsLeakProtectionAsync().ConfigureAwait(false);
                await RestoreAdapterDnsBackupAsync().ConfigureAwait(false);
                await SetWindowsRoutesAsync(adapterName, ipsToRoute, _currentAssignedIp, false).ConfigureAwait(false);

                if (_adapter != null)
                {
                    try
                    {
                        _adapter.Dispose();
                        _adapter = null;
                    }
                    catch { }
                }

                if (_trafficTask != null)
                {
                    try
                    {
                        await _trafficTask.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                    }
                    catch { }
                    _trafficTask = null;
                }

                await OctopusEngine.Current.DisposeAsync().ConfigureAwait(false);

                var deleteHostTasks = new List<Task>();
                foreach (var ip in ipsToRoute)
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

                _currentAssignedIp = "";
                ResetSavedPhysicalGateway();
                AppLogger.Log("[SYSTEM] VPN cleanup complete.");
                OnLogUpdated?.Invoke(VpnStatusMessages.VpnDisconnectedSystem);
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"[STOP ERROR] {ex.Message}");
            }

            UpdateState(AppVpnState.Disconnected);
        }
        finally
        {
            if (lockTaken)
            {
                try
                {
                    _ = _vpnGate.Release();
                }
                catch { }
            }
        }
    }
}
