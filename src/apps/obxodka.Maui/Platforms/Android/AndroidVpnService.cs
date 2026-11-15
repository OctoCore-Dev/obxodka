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
    private readonly Action<string> _statusMessageHandler;
    private readonly Action<string> _certRevokedHandler;
    private readonly Action<int> _mtuUpdatedHandler;
    private readonly Action<long, long> _trafficUpdatedHandler;
    private long _lastTrafficSent;
    private long _lastTrafficReceived;
    private long _lastTrafficTicks;

    private AndroidVpnService()
    {
        _statusMessageHandler = msg => OnLogUpdated?.Invoke(msg);
        _certRevokedHandler = msg => OnForceLogoutRequested?.Invoke(msg);
        _mtuUpdatedHandler = mtu => OctopusVpnService.Instance?.OnMtuUpdated(mtu);
        _trafficUpdatedHandler = HandleEngineTrafficUpdated;

        OctopusEngine.Current.OnStatusMessage += _statusMessageHandler;
        OctopusEngine.OnCertificateRevoked += _certRevokedHandler;
        OctopusEngine.Current.OnMtuUpdated -= _mtuUpdatedHandler;
        OctopusEngine.Current.OnMtuUpdated += _mtuUpdatedHandler;
        OctopusEngine.Current.OnTrafficUpdated -= _trafficUpdatedHandler;
        OctopusEngine.Current.OnTrafficUpdated += _trafficUpdatedHandler;
        OctopusEngine.Current.OnDeadConnectionDetected -= HandleDeadConnection;
        OctopusEngine.Current.OnDeadConnectionDetected += HandleDeadConnection;
        OctopusEngine.Current.OnUplinkLost -= HandleUplinkLost;
        OctopusEngine.Current.OnUplinkLost += HandleUplinkLost;
        OctopusEngine.Current.OnUplinkRestored -= HandleUplinkRestored;
        OctopusEngine.Current.OnUplinkRestored += HandleUplinkRestored;
        OctopusEngine.Current.OnTunnelIpChanged -= HandleTunnelIpChanged;
        OctopusEngine.Current.OnTunnelIpChanged += HandleTunnelIpChanged;
    }

    //обновление tun интерфейса android при смене внутреннего ip во время ротации протокола
    private void HandleTunnelIpChanged()
    {
        if (_isExplicitlyStopped)
        {
            return;
        }

        _ = OctopusVpnService.Instance?.EstablishTun();
    }

    //трансляция счетчиков трафика ядра в событие интерфейса vpn сервиса
    private void HandleEngineTrafficUpdated(long sentBytes, long receivedBytes)
    {
        var nowTicks = Environment.TickCount64;
        var prevTicks = Interlocked.Exchange(ref _lastTrafficTicks, nowTicks);
        var prevSent = Interlocked.Exchange(ref _lastTrafficSent, sentBytes);
        var prevReceived = Interlocked.Exchange(ref _lastTrafficReceived, receivedBytes);

        if (prevTicks <= 0 || nowTicks <= prevTicks)
        {
            return;
        }

        var elapsedSec = (nowTicks - prevTicks) / 1000.0;
        var upBps = sentBytes >= prevSent ? (long)((sentBytes - prevSent) / elapsedSec) : 0;
        var downBps = receivedBytes >= prevReceived ? (long)((receivedBytes - prevReceived) / elapsedSec) : 0;
        OnTrafficUpdated?.Invoke(new AppTrafficStats
        {
            UploadSpeedBps = upBps,
            DownloadSpeedBps = downBps
        });
    }

    //обработка потери физической сети на android
    private void HandleUplinkLost()
    {
        if (!IsRunning || _isExplicitlyStopped)
        {
            return;
        }

        AppLogger.LogWarning("[ANDROID-VPN] Uplink lost during active VPN session. Pausing tunnel...");
        OnLogUpdated?.Invoke("Физическая сеть отключена. Ожидание сети...");
        ChangeState(AppVpnState.Reconnecting);
    }

    //обработка восстановления физической сети на android
    private void HandleUplinkRestored()
    {
        if (CurrentState != AppVpnState.Reconnecting || _isExplicitlyStopped)
        {
            return;
        }

        AppLogger.Log("[ANDROID-VPN] Uplink restored on Android. Resuming connection...");
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
                //если физическая сеть отключена — не крутим циклы перебора серверов
                if (!OctopusEngine.IsUplinkAvailable)
                {
                    AppLogger.LogWarning("[DEAD CONNECTION] Physical network is down on Android. Suspending failover loops.");
                    OnLogUpdated?.Invoke("Физическая сеть отключена. Ожидание подключения...");
                    ChangeState(AppVpnState.Reconnecting);
                    return;
                }

                AppLogger.LogWarning("[DEAD CONNECTION] Packet blackhole detected on Android. Initiating circular protocol recovery...");
                OnLogUpdated?.Invoke(VpnStatusMessages.PacketBlockDetected);
                ChangeState(AppVpnState.Reconnecting);

                try
                {
                    if (await OctopusEngine.Current.TryRotateProtocolAsync("DeadConnectionRecovery").ConfigureAwait(false))
                    {
                        var activeIp = OctopusEngine.Current.ConnectedServerIp;
                        if (IPAddress.TryParse(activeIp, out _))
                        {
                            _currentServerIp = activeIp;
                        }
                        OctopusEngine.Current.ResetTrafficCounters();
                        OctopusEngine.Current.ArmTrafficWatchdog();
                        ChangeState(AppVpnState.Connected);
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
                        _ = OctopusVpnService.Instance?.EstablishTun();
                        var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(5000), forceFreshProbe: true).ConfigureAwait(false);
                        if (_isExplicitlyStopped)
                        {
                            return;
                        }

                        if (verified)
                        {
                            var activeIp = OctopusEngine.Current.ConnectedServerIp;
                            if (IPAddress.TryParse(activeIp, out _))
                            {
                                _currentServerIp = activeIp;
                            }
                            OctopusEngine.Current.ResetTrafficCounters();
                            OctopusEngine.Current.ArmTrafficWatchdog();
                            ChangeState(AppVpnState.Connected);
                            OnLogUpdated?.Invoke(VpnStatusMessages.SwitchedToBackupServerSuccess);
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
                SetError("Соединение заблокировано оператором связи (0 RX по всем протоколам).");
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
                    for (var i = 0; i < 5; i++)
                    {
                        if (i > 0)
                        {
                            var backoffMs = Math.Min(1000 * (1 << Math.Min(i - 1, 4)), 12000);
                            await Task.Delay(backoffMs).ConfigureAwait(false);
                        }

                        if (_isExplicitlyStopped || !OctopusEngine.IsUplinkAvailable)
                        {
                            return;
                        }

                        try
                        {
                            await OctopusEngine.Current.ReconnectAsync().ConfigureAwait(false);
                            _ = OctopusVpnService.Instance?.EstablishTun();
                            var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(4500), forceFreshProbe: true).ConfigureAwait(false);
                            if (_isExplicitlyStopped)
                            {
                                return;
                            }

                            if (verified)
                            {
                                var activeIp = OctopusEngine.Current.ConnectedServerIp;
                                if (IPAddress.TryParse(activeIp, out _))
                                {
                                    _currentServerIp = activeIp;
                                }
                                OctopusEngine.Current.ResetTrafficCounters();
                                OctopusEngine.Current.ArmTrafficWatchdog();
                                ChangeState(AppVpnState.Connected);
                                return;
                            }

                            await OctopusEngine.Current.DisposeAsync().ConfigureAwait(false);
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

            var stealthHost = targetIp;
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

            var serversToTry = _fallbackServers.Count > 0
                ? _fallbackServers
                : [new VpnServerDto(serverIp, serverPort, "", true, 0, null)];

            var forceWebSocket = Preferences.Get(AppPreferenceKeys.ForceWebSocket, false);
            var ring = SniEngine.BuildProtocolRotationRing(serversToTry, serverIp, serverPort, configuredSni: null, forceWebSocket);
            if (ring.Count == 0)
            {
                throw new InvalidOperationException("Нет доступных маршрутов или протоколов для подключения.");
            }

            var vpnService = OctopusVpnService.Instance ?? await OctopusVpnService.WaitForServiceReadyAsync(2000).ConfigureAwait(false);
            var activeStep = await OctopusEngine.Current.ConnectRotationRingAsync(
                ring,
                prepareTunnelBeforeVerifyAsync: (step, stepToken) =>
                {
                    stepToken.ThrowIfCancellationRequested();
                    if (_isExplicitlyStopped)
                    {
                        throw new OperationCanceledException("Stopped by user.");
                    }

                    _currentServerIp = IPAddress.TryParse(step.Host, out _)
                        ? step.Host
                        : (OctopusEngine.Current.ConnectedServerIp ?? step.Host);
                    OnLogUpdated?.Invoke(VpnStatusMessages.ConnectingWithProtocol(step.Host, step.Port, OctopusEngine.Current.ActiveProtocolDisplayName));

                    var svc = vpnService ?? OctopusVpnService.Instance;
                    var tunOk = svc?.EstablishTun() ?? false;
                    if (!tunOk)
                    {
                        var err = OctopusVpnService.LastEstablishError ?? "Не удалось инициализировать TUN интерфейс.";
                        throw new InvalidOperationException(err);
                    }

                    OnLogUpdated?.Invoke(VpnStatusMessages.CheckingChannelDuplex);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);

            if (_isExplicitlyStopped)
            {
                return;
            }

            var curTx = OctopusEngine.Current.TotalBytesSent;
            var curRx = OctopusEngine.Current.TotalBytesReceived;
            AppLogger.Log($"[ANDROID-VPN] Circular ring connected on {activeStep.Kind} ({activeStep.Host}:{activeStep.Port}), TX={curTx} B, RX={curRx} B");
            OnLogUpdated?.Invoke(VpnStatusMessages.ChannelVerifiedSecureDuplex(curTx, curRx));

            OctopusEngine.Current.ResetTrafficCounters();
            OctopusEngine.Current.ArmTrafficWatchdog();
            ChangeState(AppVpnState.Connected);
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
        OctopusEngine.Current.OnStatusMessage -= _statusMessageHandler;
        OctopusEngine.OnCertificateRevoked -= _certRevokedHandler;
        OctopusEngine.Current.OnMtuUpdated -= _mtuUpdatedHandler;
        OctopusEngine.Current.OnTrafficUpdated -= _trafficUpdatedHandler;
        OctopusEngine.Current.OnDeadConnectionDetected -= HandleDeadConnection;
        OctopusEngine.Current.OnUplinkLost -= HandleUplinkLost;
        OctopusEngine.Current.OnUplinkRestored -= HandleUplinkRestored;
        OctopusEngine.Current.OnTunnelIpChanged -= HandleTunnelIpChanged;
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
