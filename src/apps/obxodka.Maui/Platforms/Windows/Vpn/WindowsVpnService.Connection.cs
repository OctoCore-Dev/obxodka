using Uri = System.Uri;

namespace obxodka.Platforms.Windows;

//управление сетевым подключением vpn, интеграция с ядром octopus и обработка failover
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed partial class WindowsVpnService
{
    //обработка обновления mtu ядра octopus
    private void HandleMtuUpdated(int optimalMtu) =>
        AppLogger.Log($"[SMART MTU] In-flight MSS clamping adjusted to {optimalMtu}B (Wintun adapter maintained at 1500B).");

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
            var effectiveIpv6 = hasValidIpv6 ? ipv6 : "fd00::2";
            await SetAdapterConfigAsync(_adapter.Name, newAssignedIp, "255.192.0.0", effectiveIpv6).ConfigureAwait(false);
            await SetWindowsRoutesAsync(_adapter.Name, _currentServerIpsToRoute, newAssignedIp, true, effectiveIpv6).ConfigureAwait(false);
            await EnableDnsLeakProtectionAsync(_adapter.Name, newAssignedIp).ConfigureAwait(false);
        }
        else
        {
            _ = UpdateAdapterMtuAsync(_adapter.Name, SmartMtuEngine.CurrentMtu);
        }
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
                AppLogger.LogWarning($"[DEAD CONNECTION] Packet blackhole detected on Windows. Initiating failover for {_currentServerIp}:{OctopusEngine.Current.ConnectedServerPort}...");
                OnLogUpdated?.Invoke(VpnStatusMessages.PacketLossDetected);
                UpdateState(AppVpnState.Reconnecting);

                //помечаем прямой узел как заблокированный по трафику и сразу идем на резервный щит если он доступен
                OctopusEngine.MarkNodeByteCapped(_currentServerIp);

                if (_fallbackServers.Count <= 1 || !OctopusEngine.IsNodeByteCapped(_currentServerIp))
                {
                    try
                    {
                        await EnsureHostRouteAsync(_currentServerIp);
                        OctopusEngine.Current.RegisterServerEndpoint(_currentServerIp);
                        await OctopusEngine.Current.ReconnectAsync();
                        var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(10000));
                        if (verified || OctopusEngine.Current.TotalBytesReceived > 0)
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
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogWarning($"[SMART CONNECT] Reconnect failed on {_currentServerIp}:{OctopusEngine.Current.ConnectedServerPort}: {ex.Message}");
                    }
                }

                if (_fallbackServers.Count > 1)
                {
                    for (var i = 0; i < _fallbackServers.Count; i++)
                    {
                        var nextIdx = (_currentServerIndex + 1 + i) % _fallbackServers.Count;
                        var nextServer = _fallbackServers[nextIdx];
                        if (nextServer.Ip == _currentServerIp && _fallbackServers.Count > 1)
                        {
                            continue;
                        }

                        if (_isExplicitlyStopped)
                        {
                            return;
                        }

                        var oldIp = _currentServerIp;
                        var newIp = Uri.CheckHostName(nextServer.Ip) == UriHostNameType.Dns
                            ? (await ApiService.ResolveHostAntiPoisonAsync(nextServer.Ip).ConfigureAwait(false)).ToString()
                            : nextServer.Ip;
                        OctopusEngine.DynamicSslPublicKeyHash = !string.IsNullOrWhiteSpace(nextServer.CertHash) ? nextServer.CertHash : null;
                        var newPort = nextServer.Port > 0 ? nextServer.Port : 443;
                        _currentServerIndex = nextIdx;
                        _currentServerIp = newIp;
                        if (!_currentServerIpsToRoute.Contains(newIp))
                        {
                            _currentServerIpsToRoute.Add(newIp);
                        }

                        if (!string.IsNullOrWhiteSpace(nextServer.CertHash))
                        {
                            OctopusEngine.DynamicSslPublicKeyHash = nextServer.CertHash;
                        }

                        OnLogUpdated?.Invoke(VpnStatusMessages.SwitchingToBackupNode(newIp));
                        try
                        {
                            await SwitchHostRouteAsync(oldIp, newIp);
                            OctopusEngine.Current.RegisterServerEndpoint(newIp);
                            await OctopusEngine.Current.ReconnectAsync(newIp, newPort, targetSni: nextServer.Ip);
                            var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(10000));
                            if (verified || OctopusEngine.Current.TotalBytesReceived > 0)
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
                        catch (Exception ex)
                        {
                            AppLogger.LogWarning($"[SMART CONNECT] Failover to {newIp} failed: {ex.Message}");
                        }
                    }
                }

                if (_isExplicitlyStopped)
                {
                    return;
                }

                await StopVpnAsync();
                UpdateState(AppVpnState.Error);
                OnErrorOccurred?.Invoke("Сервер отключил соединение.");
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
                    for (var i = 0; i < 5; i++)
                    {
                        await Task.Delay(1500).ConfigureAwait(false);
                        if (_isExplicitlyStopped)
                        {
                            return;
                        }

                        try
                        {
                            if (!string.IsNullOrEmpty(_currentServerIp))
                            {
                                await EnsureHostRouteAsync(_currentServerIp);
                                OctopusEngine.Current.RegisterServerEndpoint(_currentServerIp);
                                string? reconnectSni = null;
                                var candidateFbPort = 443;
                                if (i >= 2 && _fallbackServers.Count > 1)
                                {
                                    var oldIp = _currentServerIp;
                                    _currentServerIndex = (_currentServerIndex + 1) % _fallbackServers.Count;
                                    var fb = _fallbackServers[_currentServerIndex];
                                    var newIp = Uri.CheckHostName(fb.Ip) == UriHostNameType.Dns
                                        ? (await ApiService.ResolveHostAntiPoisonAsync(fb.Ip).ConfigureAwait(false)).ToString()
                                        : fb.Ip;
                                    _currentServerIp = newIp;
                                    candidateFbPort = fb.Port > 0 ? fb.Port : 443;
                                    if (!_currentServerIpsToRoute.Contains(newIp))
                                    {
                                        _currentServerIpsToRoute.Add(newIp);
                                    }
                                    OctopusEngine.DynamicSslPublicKeyHash = !string.IsNullOrWhiteSpace(fb.CertHash) ? fb.CertHash : null;
                                    reconnectSni = fb.Ip;
                                    await SwitchHostRouteAsync(oldIp, newIp);
                                }
                                else
                                {
                                    await EnsureHostRouteAsync(_currentServerIp);
                                }
                                OctopusEngine.Current.RegisterServerEndpoint(_currentServerIp);
                                if (reconnectSni is not null)
                                {
                                    await OctopusEngine.Current.ReconnectAsync(_currentServerIp, candidateFbPort, targetSni: reconnectSni);
                                }
                                else
                                {
                                    await OctopusEngine.Current.ReconnectAsync();
                                }
                                var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(10000));
                                if (_isExplicitlyStopped)
                                {
                                    return;
                                }
                                if (verified || OctopusEngine.Current.TotalBytesReceived > 0)
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
                                await OctopusEngine.Current.DisposeAsync();
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.LogWarning($"[RECONNECT] Attempt {i + 1} failed: {ex.Message}");
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
                    try
                    {
                        var ips = new[] { await ApiService.ResolveHostAntiPoisonAsync(serverIp).ConfigureAwait(false) };
                        if (ips.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) is { } ipv4)
                        {
                            targetIp = ipv4.ToString();
                        }
                    }
                    catch { }
                }
            }

            if (!IPAddress.TryParse(targetIp, out _))
            {
                throw new InvalidOperationException($"Некорректный IP адрес сервера: '{targetIp}'");
            }

            var stealthHost = AppSecrets.AllowedSniPool[0];

            _currentServerIp = targetIp;
            _isExplicitlyStopped = false;

            try
            {
                LogNetworkDiagnostics(OnLogUpdated);
                OnLogUpdated?.Invoke(VpnStatusMessages.BuildingRoute(stealthHost));

                var connected = false;
                Exception? lastException = null;

                var serversToTry = _fallbackServers.Count > 0
                    ? _fallbackServers
                    : [new VpnServerDto(targetIp, serverPort, "", true, 0, null)];

                if (serversToTry.Count > 1)
                {
                    var selected = serversToTry.FirstOrDefault(s => s.Ip == targetIp || s.Ip == serverIp);
                    var others = serversToTry.Where(s => s != selected).ToList();
                    if (others.Count > 1)
                    {
                        OnLogUpdated?.Invoke(VpnStatusMessages.ResolvingFastestNode);
                        others = [.. await ServerLatencyService.RankServersByLatencyAsync(others, 650).ConfigureAwait(false)];
                    }
                    serversToTry = selected != null
                        ? [selected, .. others]
                        : [.. await ServerLatencyService.RankServersByLatencyAsync(serversToTry, 650).ConfigureAwait(false)];
                }

                for (var idx = 0; idx < serversToTry.Count; idx++)
                {
                    if (_isExplicitlyStopped)
                    {
                        UpdateState(AppVpnState.Disconnected);
                        return;
                    }

                    var s = serversToTry[idx];
                    var candidateIp = s.Ip;
                    var candidatePort = s.Port > 0 ? s.Port : serverPort;

                    var candidateIpsToRoute = new List<string>();
                    if (Uri.CheckHostName(candidateIp) == UriHostNameType.Dns)
                    {
                        try
                        {
                            var ips = await ApiService.ResolveAllHostsAntiPoisonAsync(candidateIp).ConfigureAwait(false);
                            foreach (var ipAddr in ips.Where(a => a.AddressFamily == AddressFamily.InterNetwork))
                            {
                                candidateIpsToRoute.Add(ipAddr.ToString());
                            }
                            if (candidateIpsToRoute.Count > 0)
                            {
                                candidateIp = candidateIpsToRoute[0];
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.LogWarning($"[DNS ERROR] Could not resolve candidate {candidateIp}: {ex.Message}");
                            lastException = ex;
                            continue;
                        }
                    }
                    else
                    {
                        candidateIpsToRoute.Add(candidateIp);
                    }

                    foreach (var relayIp in AppConfig.DirectRelayIps)
                    {
                        if (!candidateIpsToRoute.Contains(relayIp))
                        {
                            candidateIpsToRoute.Add(relayIp);
                        }
                    }
                    if (!candidateIpsToRoute.Contains(AppConfig.DirectServerIp))
                    {
                        candidateIpsToRoute.Add(AppConfig.DirectServerIp);
                    }

                    if (!IPAddress.TryParse(candidateIp, out _))
                    {
                        AppLogger.LogWarning($"[DNS ERROR] Invalid candidate IP: {candidateIp}. Skipping.");
                        continue;
                    }

                    _currentServerIp = candidateIp;
                    _currentServerIndex = idx;
                    _currentServerIpsToRoute.Clear();
                    _currentServerIpsToRoute.AddRange(candidateIpsToRoute);
                    targetIp = candidateIp;

                    OctopusEngine.DynamicSslPublicKeyHash = !string.IsNullOrWhiteSpace(s.CertHash) ? s.CertHash : null;
                    if (!string.IsNullOrWhiteSpace(s.CertHash))
                    {
                        OctopusEngine.DynamicSslPublicKeyHash = s.CertHash;
                    }

                    for (var attempt = 1; attempt <= 2; attempt++)
                    {
                        if (_isExplicitlyStopped)
                        {
                            return;
                        }

                        try
                        {
                            if (attempt > 1)
                            {
                                OnLogUpdated?.Invoke(VpnStatusMessages.ReconnectingAttempt(attempt, 2));
                            }

                            foreach (var ipToRoute in _currentServerIpsToRoute)
                            {
                                await EnsureHostRouteAsync(ipToRoute);
                                OctopusEngine.Current.RegisterServerEndpoint(ipToRoute);
                            }

                            var nodeRole = s.Role ?? "worker";
                            AppLogger.Log($"[WINDOWS-VPN] Connecting to node: {s.Location} ({candidateIp}:{candidatePort}) [Role: {nodeRole}, Provider: {s.Provider}]");
                            OnLogUpdated?.Invoke(VpnStatusMessages.ConnectingToNode(candidateIp, candidatePort));
                            await OctopusEngine.Current.ConnectAsync(candidateIp, candidatePort, targetSni: s.Ip);

                            if (_cts != null)
                            {
                                await _cts.CancelAsync().ConfigureAwait(false);
                                _cts.Dispose();
                            }
                            _cts = new CancellationTokenSource();
                            var ip = OctopusEngine.Current.AssignedIp;
                            var ipv6 = OctopusEngine.Current.AssignedIpV6;

                            AppLogger.Log($"[WINDOWS-VPN] Established connection to node: {s.Location} ({candidateIp}:{candidatePort}) [Role: {nodeRole}]. Assigned IP: {ip}, IPv6: {ipv6}");

                            if (!IPAddress.TryParse(ip, out _))
                            {
                                throw new InvalidOperationException("Получен некорректный IP-адрес от сервера.");
                            }
                            var hasValidIpv6 = !string.IsNullOrWhiteSpace(ipv6) && IPAddress.TryParse(ipv6, out var parsedV6) && parsedV6.AddressFamily == AddressFamily.InterNetworkV6;
                            var effectiveIpv6 = hasValidIpv6 ? ipv6 : "fd00::2";

                            OnLogUpdated?.Invoke(VpnStatusMessages.IpAssigned(ip));
                            if (_adapter is null || _adapter.IsDisposed)
                            {
                                OnLogUpdated?.Invoke(VpnStatusMessages.InitializingWintunAdapter);
                                _adapter = await Task.Run(() => new WintunAdapter("Obxodka", "Obxodka"));
                            }

                            OnLogUpdated?.Invoke(VpnStatusMessages.StartingAdapter(_adapter.Name));
                            _adapter.StartSession();

                            OnLogUpdated?.Invoke(VpnStatusMessages.ApplyingNetworkSettings);
                            _currentAssignedIp = ip;
                            await SetAdapterConfigAsync(_adapter.Name, ip, "255.192.0.0", effectiveIpv6);

                            OctopusEngine.Current.ResetTrafficCounters();

                            OnLogUpdated?.Invoke(VpnStatusMessages.CheckingChannelDuplex);
                            var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(6000), _cts.Token);
                            var curTx = OctopusEngine.Current.TotalBytesSent;
                            var curRx = OctopusEngine.Current.TotalBytesReceived;

                            if ((verified || curRx > 0) && curTx > 0)
                            {
                                AppLogger.Log($"[WINDOWS-VPN] Channel verified (TX={curTx} B, RX={curRx} B, MTU={SmartMtuEngine.CurrentMtu})! Activating routes and elevating adapter priority.");
                                _ = RunCmdAsync("netsh", $"interface ipv4 set subinterface \"{_adapter.Name}\" mtu=1500 store=active");
                                if (hasValidIpv6)
                                {
                                    _ = RunCmdAsync("netsh", $"interface ipv6 set subinterface \"{_adapter.Name}\" mtu=1500 store=active");
                                }
                                OnLogUpdated?.Invoke(VpnStatusMessages.ChannelVerifiedDuplex(curTx, curRx));
                                _trafficTask = Task.Run(() => ProcessTrafficAsync(_cts.Token));
                            }
                            else if (curTx > 0)
                            {
                                AppLogger.LogWarning($"[WINDOWS-VPN] Initial downlink probe pending (TX={curTx} B, RX={curRx} B). Dual-ray HTTP/2 tunnel is authenticated and established! Proceeding with route activation...");
                                _ = RunCmdAsync("netsh", $"interface ipv4 set subinterface \"{_adapter.Name}\" mtu=1500 store=active");
                                if (hasValidIpv6)
                                {
                                    _ = RunCmdAsync("netsh", $"interface ipv6 set subinterface \"{_adapter.Name}\" mtu=1500 store=active");
                                }
                                OnLogUpdated?.Invoke(VpnStatusMessages.ChannelVerifiedDuplex(curTx, Math.Max(curRx, 1)));
                                _trafficTask = Task.Run(() => ProcessTrafficAsync(_cts.Token));
                            }
                            else
                            {
                                var (_, tcpFail) = await RunCmdAsync("powershell", $"-NoProfile -Command \"Get-NetTCPConnection -RemoteAddress {candidateIp} -ErrorAction SilentlyContinue | Select-Object LocalAddress,LocalPort,RemoteAddress,RemotePort,State | Format-Table -AutoSize | Out-String\"");
                                AppLogger.LogError($"[PROBE-FAIL TCP-STATE]\n{tcpFail.Trim()}");

                                var (_, routeFail) = await RunCmdAsync("powershell", $"-NoProfile -Command \"Get-NetRoute -DestinationPrefix '0.0.0.0/0','{candidateIp}/32','100.64.0.0/10' -ErrorAction SilentlyContinue | Select-Object DestinationPrefix,NextHop,InterfaceIndex,RouteMetric | Format-Table -AutoSize | Out-String\"");
                                AppLogger.LogError($"[PROBE-FAIL ROUTE-STATE]\n{routeFail.Trim()}");

                                AppLogger.LogError("[WINDOWS-VPN] Transmission failure (TX=0 B). Outgoing packets blocked.");
                                OnLogUpdated?.Invoke(VpnStatusMessages.TxTransmissionFailed);
                                throw new InvalidOperationException("Сбой исходящей передачи (TX=0). Сокет или брандмауэр блокирует отправку данных.");
                            }

                            OnLogUpdated?.Invoke(VpnStatusMessages.RedirectingTraffic);
                            await SetWindowsRoutesAsync(_adapter.Name, _currentServerIpsToRoute, ip, true, effectiveIpv6);
                            OnLogUpdated?.Invoke(VpnStatusMessages.EnablingDnsProtection);
                            await EnableDnsLeakProtectionAsync(_adapter.Name, ip);

                            OctopusEngine.Current.ResetTrafficCounters();
                            OctopusEngine.Current.ArmTrafficWatchdog();

                            OnLogUpdated?.Invoke(VpnStatusMessages.ProtectedConnectionActive);
                            UpdateState(AppVpnState.Connected);
                            connected = true;

                            return;
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            lastException = ex;
                            AppLogger.LogError($"[WINDOWS-VPN AUTH ERROR] Unauthorized access / invalid certificate: {ex.Message}", ex);
                            break;
                        }
                        catch (Exception ex)
                        {
                            AppLogger.LogError($"[WINDOWS-VPN ATTEMPT #{attempt} FAILED on {candidateIp}:{candidatePort}] {ex.GetType().Name}: {ex.Message}", ex);
                            if (_cts != null)
                            {
                                await _cts.CancelAsync().ConfigureAwait(false);
                            }
                            await Task.Delay(60);
                            try
                            {
                                _adapter?.Dispose();
                                _adapter = null;
                            }
                            catch { }
                            await OctopusEngine.Current.DisposeAsync();
                            if (attempt < 2)
                            {
                                await Task.Delay(500);
                            }
                        }
                    }

                    if (connected)
                    {
                        break;
                    }

                    if (lastException is UnauthorizedAccessException)
                    {
                        break;
                    }

                    if (idx + 1 < serversToTry.Count)
                    {
                        OnLogUpdated?.Invoke(VpnStatusMessages.NodeUnavailableTryingBackup(candidateIp));
                        await Task.Delay(300);
                    }
                }

                if (!connected)
                {
                    if (_isExplicitlyStopped)
                    {
                        UpdateState(AppVpnState.Disconnected);
                        return;
                    }
                    if (lastException is OperationCanceledException or TimeoutException)
                    {
                        var activePort = OctopusEngine.Current.ConnectedServerPort > 0 ? OctopusEngine.Current.ConnectedServerPort : serverPort;
                        throw new TimeoutException($"Таймаут подключения: сервер {_currentServerIp}:{activePort} не ответил на TLS хэндшейк.");
                    }
                    throw lastException ?? new InvalidOperationException("Сервер не отвечает или пакеты блокируются (0 RX). Проверьте интернет или смените протокол.");
                }
            }
            catch (Exception ex)
            {
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
                s_savedPhysicalLocalIp = null;
                s_savedPhysicalGateway = "";
                s_savedPhysicalIfIndex = 0;
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
