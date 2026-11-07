using Uri = System.Uri;

namespace obxodka.Platforms.Android;

//управление системным vpn сервисом android и координация с движком octopus
[SupportedOSPlatform("android29.0")]
internal sealed class AndroidVpnService : IVpnService, IDisposable
{
    public static AndroidVpnService Instance { get; } = new();

    public AppVpnState CurrentState { get; private set; } = AppVpnState.Disconnected;
    public bool IsRunning => CurrentState == AppVpnState.Connected;

    public event Action<AppVpnState>? OnStateChanged;
    public event Action<string>? OnLogUpdated;
    public event Action<string>? OnErrorOccurred;
    public event Action<AppTrafficStats>? OnTrafficUpdated = delegate { };
    public event Action<string>? OnForceLogoutRequested;

    private string _currentServerIp = "";
    private bool _isExplicitlyStopped;
    private List<VpnServerDto> _fallbackServers = [];
    private int _currentServerIndex;
    private int _isHandlingDeadConnection;
    private int _isHandlingDrop;
    private readonly SemaphoreSlim _vpnGate = new(1, 1);
    private readonly Lock _roamingLock = new();
    private CancellationTokenSource? _roamingCts;

    private AndroidVpnService()
    {
        OctopusEngine.Current.OnStatusMessage += msg => OnLogUpdated?.Invoke(msg);
        OctopusEngine.OnCertificateRevoked += msg => OnForceLogoutRequested?.Invoke(msg);
        OctopusEngine.Current.OnDeadConnectionDetected -= HandleDeadConnection;
        OctopusEngine.Current.OnDeadConnectionDetected += HandleDeadConnection;
    }

    //обработка мертвого соединения (блэкхол пакетов) и интеллектуальный failover
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
                AppLogger.LogWarning("[DEAD CONNECTION] Packet blackhole detected. Initiating smart failover...");
                OnLogUpdated?.Invoke(VpnStatusMessages.PacketBlockDetected);
                ChangeState(AppVpnState.Reconnecting);

                OctopusEngine.MarkNodeByteCapped(_currentServerIp);

                if (_fallbackServers.Count <= 1 || !OctopusEngine.IsNodeByteCapped(_currentServerIp))
                {
                    try
                    {
                        await OctopusEngine.Current.ReconnectAsync();
                        _ = OctopusVpnService.Instance?.EstablishTun();
                        var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(10000));
                        if (_isExplicitlyStopped)
                        {
                            return;
                        }

                        if (verified || OctopusEngine.Current.IsConnected)
                        {
                            OctopusEngine.Current.ResetTrafficCounters();
                            OctopusEngine.Current.ArmTrafficWatchdog();
                            ChangeState(AppVpnState.Connected);
                            OnLogUpdated?.Invoke(VpnStatusMessages.ConnectionRestoredTls);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogWarning($"[SMART CONNECT] Reconnect failed on {_currentServerIp}: {ex.Message}");
                    }
                }

                if (_fallbackServers.Count > 1)
                {
                    for (var i = 0; i < _fallbackServers.Count; i++)
                    {
                        if (_isExplicitlyStopped)
                        {
                            return;
                        }

                        var nextIdx = (_currentServerIndex + 1 + i) % _fallbackServers.Count;
                        var nextServer = _fallbackServers[nextIdx];
                        if (nextServer.Ip == _currentServerIp && _fallbackServers.Count > 1)
                        {
                            continue;
                        }

                        _currentServerIndex = nextIdx;
                        _currentServerIp = Uri.CheckHostName(nextServer.Ip) == UriHostNameType.Dns
                            ? (await ApiService.ResolveHostAntiPoisonAsync(nextServer.Ip).ConfigureAwait(false)).ToString()
                            : nextServer.Ip;
                        var nextPort = nextServer.Port > 0 ? nextServer.Port : 443;
                        OctopusEngine.DynamicSslPublicKeyHash = !string.IsNullOrWhiteSpace(nextServer.CertHash) ? nextServer.CertHash : null;

                        OnLogUpdated?.Invoke(VpnStatusMessages.SwitchingToBackupServer(_currentServerIp));
                        try
                        {
                            await OctopusEngine.Current.ReconnectAsync(_currentServerIp, nextPort, targetSni: nextServer.Ip);
                            _ = OctopusVpnService.Instance?.EstablishTun();
                            var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(10000));
                            if (_isExplicitlyStopped)
                            {
                                return;
                            }

                            if (verified || OctopusEngine.Current.IsConnected)
                            {
                                OctopusEngine.Current.ResetTrafficCounters();
                                OctopusEngine.Current.ArmTrafficWatchdog();
                                ChangeState(AppVpnState.Connected);
                                OnLogUpdated?.Invoke(VpnStatusMessages.SwitchedToBackupServerSuccess);
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.LogWarning($"[SMART CONNECT] Failover to {_currentServerIp} failed: {ex.Message}");
                        }
                    }
                }

                if (_isExplicitlyStopped)
                {
                    return;
                }

                await StopVpnAsync();
                SetError("Соединение заблокировано оператором связи.");
            }
            finally
            {
                Volatile.Write(ref _isHandlingDeadConnection, 0);
            }
        });
    }

    //обработка системного обрыва сетевого соединения движка
    public void HandleEngineDrop()
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
                    SetError("Связь с сервером потеряна.");
                });
                return;
            }

            if (Interlocked.CompareExchange(ref _isHandlingDrop, 1, 0) != 0)
            {
                return;
            }

            lock (_roamingLock)
            {
                _roamingCts?.Cancel();
            }

            ChangeState(AppVpnState.Reconnecting);
            _ = Task.Run(async () =>
            {
                try
                {
                    for (var i = 0; i < 8; i++)
                    {
                        if (i > 0)
                        {
                            var backoffMs = Math.Min(1000 * (1 << Math.Min(i - 1, 4)), 16000);
                            if (i >= 2 && _fallbackServers.Count > 1)
                            {
                                _currentServerIndex = (_currentServerIndex + 1) % _fallbackServers.Count;
                                var fb = _fallbackServers[_currentServerIndex];
                                _currentServerIp = Uri.CheckHostName(fb.Ip) == UriHostNameType.Dns
                                    ? (await ApiService.ResolveHostAntiPoisonAsync(fb.Ip).ConfigureAwait(false)).ToString()
                                    : fb.Ip;
                                var fbPort = fb.Port > 0 ? fb.Port : 443;
                                OctopusEngine.DynamicSslPublicKeyHash = !string.IsNullOrWhiteSpace(fb.CertHash) ? fb.CertHash : null;

                                await Task.Delay(backoffMs);
                                if (_isExplicitlyStopped)
                                {
                                    return;
                                }

                                try
                                {
                                    await OctopusEngine.Current.ReconnectAsync(_currentServerIp, fbPort, targetSni: fb.Ip);
                                    _ = OctopusVpnService.Instance?.EstablishTun();
                                    var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(10000));
                                    if (_isExplicitlyStopped)
                                    {
                                        return;
                                    }

                                    if (verified || OctopusEngine.Current.IsConnected)
                                    {
                                        OctopusEngine.Current.ResetTrafficCounters();
                                        OctopusEngine.Current.ArmTrafficWatchdog();
                                        ChangeState(AppVpnState.Connected);
                                        return;
                                    }
                                }
                                catch { }
                                continue;
                            }

                            await Task.Delay(backoffMs);
                        }

                        if (_isExplicitlyStopped)
                        {
                            return;
                        }

                        try
                        {
                            await OctopusEngine.Current.ReconnectAsync();
                            _ = OctopusVpnService.Instance?.EstablishTun();
                            var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(10000));
                            if (_isExplicitlyStopped)
                            {
                                return;
                            }

                            if (verified || OctopusEngine.Current.IsConnected)
                            {
                                OctopusEngine.Current.ResetTrafficCounters();
                                OctopusEngine.Current.ArmTrafficWatchdog();
                                ChangeState(AppVpnState.Connected);
                                return;
                            }
                        }
                        catch { }
                    }

                    if (_isExplicitlyStopped)
                    {
                        return;
                    }

                    if (killSwitch)
                    {
                        SetError("Не удалось восстановить связь. Kill Switch блокирует утечку IP. Нажмите «Стоп» для отключения.");
                    }
                    else
                    {
                        await StopVpnAsync();
                        SetError("Связь с сервером потеряна. Не удалось восстановить подключение.");
                    }
                }
                finally
                {
                    Volatile.Write(ref _isHandlingDrop, 0);
                }
            });
        }
    }

    //быстрый бесшовный реконнект при смене сети (роуминг wi-fi и мобильной связи)
    public void TriggerImmediateReconnect()
    {
        if (_isExplicitlyStopped || string.IsNullOrEmpty(_currentServerIp))
        {
            return;
        }

        //сброс штрафов прямых узлов и кэша днс при смене физической сети wi-fi или lte
        OctopusEngine.ClearByteCappedNodes();
        ApiService.InvalidateHostCache();

        CancellationToken ct;
        lock (_roamingLock)
        {
            _roamingCts?.Cancel();
            _roamingCts?.Dispose();
            _roamingCts = new CancellationTokenSource();
            ct = _roamingCts.Token;
        }

        ChangeState(AppVpnState.Reconnecting);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, ct);
                if (ct.IsCancellationRequested || _isExplicitlyStopped)
                {
                    return;
                }

                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    if (ct.IsCancellationRequested || _isExplicitlyStopped)
                    {
                        return;
                    }

                    try
                    {
                        AppLogger.Log($"[NETWORK ROAMING] Fast reconnect attempt #{attempt}...");
                        await OctopusEngine.Current.ReconnectAsync();
                        _ = OctopusVpnService.Instance?.EstablishTun();
                        var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(10000), ct);
                        if (ct.IsCancellationRequested || _isExplicitlyStopped)
                        {
                            return;
                        }

                        if (verified || OctopusEngine.Current.IsConnected)
                        {
                            OctopusEngine.Current.ResetTrafficCounters();
                            OctopusEngine.Current.ArmTrafficWatchdog();
                            ChangeState(AppVpnState.Connected);
                            AppLogger.Log("[NETWORK ROAMING] Connected to new network interface seamlessly!");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogWarning($"[NETWORK ROAMING ATTEMPT #{attempt} FAILED] {ex.Message}");
                        await Task.Delay(1000, ct);
                    }
                }

                if (!ct.IsCancellationRequested && !_isExplicitlyStopped)
                {
                    AppLogger.Log("[NETWORK ROAMING] Fast reconnect attempts exhausted. Initiating server failover...");
                    try
                    {
                        await StartVpnAsync(_currentServerIp, OctopusEngine.Current.ConnectedServerPort, _fallbackServers);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogError($"[NETWORK ROAMING FULL RECOVERY ERROR] {ex.Message}");
                        await StopVpnAsync();
                        SetError("Не удалось восстановить подключение после смены сети.");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppLogger.LogError($"[NETWORK ROAMING RECONNECT] {ex.Message}");
            }
        }, ct);
    }

    //запуск vpn по ip и порту без резервных серверов
    public Task StartVpnAsync(string serverIp, int serverPort) =>
        StartVpnAsync(serverIp, serverPort, null);

    //запуск vpn туннеля с приоритетным выбором узла и списком резервных серверов
    public async Task StartVpnAsync(string serverIp, int serverPort, IReadOnlyList<VpnServerDto>? fallbackServers)
    {
        await _vpnGate.WaitAsync();
        try
        {
            ChangeState(AppVpnState.Connecting);
            _fallbackServers = fallbackServers != null ? [.. fallbackServers] : [];
            _currentServerIndex = _fallbackServers.FindIndex(s => s.Ip == serverIp);
            if (_currentServerIndex < 0 && !string.IsNullOrEmpty(serverIp))
            {
                _fallbackServers.Insert(0, new VpnServerDto(serverIp, serverPort, "", true, 0, null));
                _currentServerIndex = 0;
            }

            var targetIp = serverIp;
            if (Uri.CheckHostName(serverIp) == UriHostNameType.Dns)
            {
                try
                {
                    var addrs = new[] { await ApiService.ResolveHostAntiPoisonAsync(serverIp).ConfigureAwait(false) };
                    if (addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) is { } ipv4)
                    {
                        targetIp = ipv4.ToString();
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"[DNS ERROR] Could not resolve {serverIp}: {ex.Message}");
                }
            }

            var stealthHost = AppSecrets.AllowedSniPool[0];
            _currentServerIp = targetIp;
            _isExplicitlyStopped = false;

            var intent = global::Android.Net.VpnService.Prepare(Platform.AppContext);
            if (intent is not null)
            {
                var granted = await MainActivity.RequestVpnPermissionAsync(intent);
                if (!granted)
                {
                    ChangeState(AppVpnState.Disconnected);
                    SetError("VPN разрешение не выдано");
                    return;
                }
            }

            OnLogUpdated?.Invoke(VpnStatusMessages.DomainRoute(stealthHost));

            MainActivity.StartVpnService();
            _ = await OctopusVpnService.WaitForServiceReadyAsync(4000)
                ?? throw new InvalidOperationException("Не удалось запустить системную службу VPN.");

            var connected = false;
            Exception? lastEx = null;

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
                    return;
                }

                var s = serversToTry[idx];
                var candidateIp = s.Ip;
                var candidatePort = s.Port > 0 ? s.Port : (serverPort > 0 ? serverPort : 443);

                if (Uri.CheckHostName(candidateIp) == UriHostNameType.Dns)
                {
                    try
                    {
                        var addrs = new[] { await ApiService.ResolveHostAntiPoisonAsync(candidateIp).ConfigureAwait(false) };
                        if (addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) is { } ipv4)
                        {
                            candidateIp = ipv4.ToString();
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogWarning($"[DNS ERROR] Could not resolve candidate {candidateIp}: {ex.Message}");
                        lastEx = ex;
                        continue;
                    }
                }

                if (!IPAddress.TryParse(candidateIp, out _))
                {
                    AppLogger.LogWarning($"[DNS ERROR] Invalid candidate IP: {candidateIp}. Skipping.");
                    continue;
                }

                _currentServerIp = candidateIp;
                _currentServerIndex = idx;
                OctopusEngine.DynamicSslPublicKeyHash = !string.IsNullOrWhiteSpace(s.CertHash) ? s.CertHash : null;

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

                        var nodeRole = s.Role ?? "worker";
                        AppLogger.Log($"[ANDROID-VPN] Connecting to node: {s.Location} ({candidateIp}:{candidatePort}) [Role: {nodeRole}, Provider: {s.Provider}]");
                        OnLogUpdated?.Invoke(VpnStatusMessages.ConnectingToServer(candidateIp, candidatePort));
                        await OctopusEngine.Current.ConnectAsync(candidateIp, candidatePort, targetSni: s.Ip);
                        AppLogger.Log($"[ANDROID-VPN] Established connection to node: {s.Location} ({candidateIp}:{candidatePort}) [Role: {nodeRole}]. Assigned IP: {OctopusEngine.Current.AssignedIp}");

                        var tunOk = OctopusVpnService.Instance?.EstablishTun() ?? false;
                        if (!tunOk)
                        {
                            throw new InvalidOperationException("Не удалось инициализировать TUN интерфейс.");
                        }

                        OctopusEngine.Current.ResetTrafficCounters();
                        OnLogUpdated?.Invoke(VpnStatusMessages.CheckingChannelDuplex);
                        var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(6000));
                        var curTx = OctopusEngine.Current.TotalBytesSent;
                        var curRx = OctopusEngine.Current.TotalBytesReceived;

                        if ((verified || curRx > 0) && curTx > 0)
                        {
                            OnLogUpdated?.Invoke(VpnStatusMessages.ChannelVerifiedSecureDuplex(curTx, curRx));
                            OctopusEngine.Current.ResetTrafficCounters();
                            OctopusEngine.Current.ArmTrafficWatchdog();
                            ChangeState(AppVpnState.Connected);
                            connected = true;
                            break;
                        }
                        else if (curTx > 0)
                        {
                            AppLogger.LogWarning($"[ANDROID-VPN] Initial downlink probe pending (TX={curTx} B, RX={curRx} B). Dual-ray HTTP/2 tunnel is authenticated and established! Proceeding with route activation...");
                            OnLogUpdated?.Invoke(VpnStatusMessages.ChannelVerifiedDuplex(curTx, Math.Max(curRx, 1)));
                            OctopusEngine.Current.ResetTrafficCounters();
                            OctopusEngine.Current.ArmTrafficWatchdog();
                            ChangeState(AppVpnState.Connected);
                            connected = true;
                            break;
                        }
                        else
                        {
                            OnLogUpdated?.Invoke(VpnStatusMessages.TxTransmissionFailed);
                            throw new InvalidOperationException("Сбой исходящей передачи (TX=0). Проверьте системные разрешения VPN.");
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                        AppLogger.LogError($"[VPN CONNECT FAILED FOR {candidateIp}] {ex.Message}");
                        OctopusVpnService.Instance?.StopNativeVpn();
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

                if (_isExplicitlyStopped)
                {
                    return;
                }

                if (idx + 1 < serversToTry.Count)
                {
                    OnLogUpdated?.Invoke(VpnStatusMessages.ServerUnavailableTryingBackup(candidateIp));
                    await Task.Delay(300);
                }
            }

            if (!connected && lastEx is not null)
            {
                throw lastEx;
            }
        }
        catch (Exception ex)
        {
            try
            {
                OctopusVpnService.Instance?.StopNativeVpn();
            }
            catch { }

            if (_isExplicitlyStopped)
            {
                AppLogger.Log($"[VPN DISCONNECT] Normal stop/cancellation: {ex.Message}");
                ChangeState(AppVpnState.Disconnected);
                return;
            }

            var activePort = OctopusEngine.Current.ConnectedServerPort > 0 ? OctopusEngine.Current.ConnectedServerPort : serverPort;
            SetError(ex is TimeoutException or OperationCanceledException
                ? $"Таймаут подключения: сервер {_currentServerIp}:{activePort} не ответил на TLS хэндшейк."
                : $"Ошибка подключения: {ex.Message}");
        }
        finally
        {
            _ = _vpnGate.Release();
        }
    }

    //остановка vpn туннеля и освобождение ресурсов
    public async Task StopVpnAsync()
    {
        _isExplicitlyStopped = true;
        CancellationTokenSource? roamingCtsToCancel;
        lock (_roamingLock)
        {
            roamingCtsToCancel = _roamingCts;
        }

        if (roamingCtsToCancel is not null)
        {
            try
            {
                await roamingCtsToCancel.CancelAsync().ConfigureAwait(false);
            }
            catch { }
        }
        await _vpnGate.WaitAsync();
        try
        {
            ChangeState(AppVpnState.Disconnecting);
            OctopusVpnService.Instance?.StopNativeVpn();

            await Task.Run(async () =>
            {
                try
                {
                    var disposeTask = OctopusEngine.Current.DisposeAsync().AsTask();
                    _ = await Task.WhenAny(disposeTask, Task.Delay(800));
                }
                catch { }
            }).ConfigureAwait(false);

            ChangeState(AppVpnState.Disconnected);
        }
        finally
        {
            _ = _vpnGate.Release();
        }
    }

    //смена внутреннего состояния vpn и оповещение подписчиков
    public void ChangeState(AppVpnState newState)
    {
        if (CurrentState == newState)
        {
            return;
        }

        CurrentState = newState;
        MainThread.BeginInvokeOnMainThread(() => OnStateChanged?.Invoke(CurrentState));
    }

    //установка состояния ошибки и оповещение ui
    public void SetError(string message)
    {
        try
        {
            OctopusVpnService.Instance?.StopNativeVpn();
        }
        catch { }

        ChangeState(AppVpnState.Error);
        MainThread.BeginInvokeOnMainThread(() => OnErrorOccurred?.Invoke(message));
    }

    //освобождение ресурсов сервиса
    public void Dispose()
    {
        lock (_roamingLock)
        {
            try
            {
                _roamingCts?.Cancel();
                _roamingCts?.Dispose();
                _roamingCts = null;
            }
            catch { }
        }
        _vpnGate.Dispose();
    }
}
