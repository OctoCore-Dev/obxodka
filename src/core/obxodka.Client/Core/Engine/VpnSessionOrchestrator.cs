namespace obxodka.Core;

//единый кроссплатформенный оркестратор vpn-сессии (ssot жизненного цикла, failover и защиты сокетов)
public sealed class VpnSessionOrchestrator : IVpnService, IDisposable
{
    private readonly ITunnelPlatformAdapter _adapter;
    private readonly Action<string> _certRevokedHandler;
    private readonly OctopusEngine _engine;
    private readonly Action<int> _mtuUpdatedHandler;
    private readonly EventHandler? _processExitHandler;
    private readonly Lock _roamingLock = new();
    private readonly Lock _routesLock = new();
    private readonly List<string> _serverIpsToRoute = [];
    private readonly Action<string> _statusMessageHandler;
    private readonly Action<long, long> _trafficUpdatedHandler;
    private readonly SemaphoreSlim _vpnGate = new(1, 1);
    private string _currentAssignedIp = "";
    private string? _currentAssignedIpV6;
    private string _currentServerIp = "";
    private int _disposed;
    private List<VpnServerDto> _fallbackServers = [];
    private int _isExplicitlyStopped;
    private int _isHandlingDeadConnection;
    private int _isHandlingDrop;
    private long _lastTrafficReceived;
    private long _lastTrafficSent;
    private long _lastTrafficTicks;
    private CancellationTokenSource? _roamingCts;

    private CancellationTokenSource? _sessionCts;
    private long _sessionEpoch;

    public VpnSessionOrchestrator(ITunnelPlatformAdapter adapter, OctopusEngine? engine = null, bool hookProcessExit = true)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        _adapter = adapter;
        _engine = engine ?? OctopusEngine.Current;

        _statusMessageHandler = msg =>
        {
            if (Volatile.Read(ref _isExplicitlyStopped) != 0 ||
                CurrentState is AppVpnState.Disconnected or AppVpnState.Disconnecting)
            {
                return;
            }

            OnLogUpdated?.Invoke(msg);
        };
        _certRevokedHandler = msg => OnForceLogoutRequested?.Invoke(msg);
        _mtuUpdatedHandler = HandleMtuUpdated;
        _trafficUpdatedHandler = HandleEngineTrafficUpdated;

        _engine.ProtectTransportSockets(_adapter.BindPhysicalSocket);
        _engine.EnsureExternalHostRoutesAsync = TrackAndPrepareBypassHostsAsync;
        _engine.OnStatusMessage += _statusMessageHandler;
        OctopusEngine.OnCertificateRevoked += _certRevokedHandler;
        _engine.OnTrafficUpdated -= _trafficUpdatedHandler;
        _engine.OnTrafficUpdated += _trafficUpdatedHandler;
        _engine.OnMtuUpdated -= _mtuUpdatedHandler;
        _engine.OnMtuUpdated += _mtuUpdatedHandler;
        _engine.OnConnectionDropped -= HandleEngineDrop;
        _engine.OnConnectionDropped += HandleEngineDrop;
        _engine.OnDeadConnectionDetected -= HandleDeadConnection;
        _engine.OnDeadConnectionDetected += HandleDeadConnection;
        _engine.OnUplinkLost -= HandleUplinkLost;
        _engine.OnUplinkLost += HandleUplinkLost;
        _engine.OnUplinkRestored -= HandleUplinkRestored;
        _engine.OnUplinkRestored += HandleUplinkRestored;
        _engine.OnTunnelIpChanged -= HandleTunnelIpChanged;
        _engine.OnTunnelIpChanged += HandleTunnelIpChanged;
        _adapter.OnPlatformNetworkChanged += HandlePlatformNetworkChanged;

        if (!hookProcessExit)
        {
            return;
        }

        _processExitHandler = (_, _) =>
        {
            try
            {
                _ = StopVpnAsync().Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // ignored
            }
        };
        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_processExitHandler is not null)
        {
            AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
        }

        _adapter.OnPlatformNetworkChanged -= HandlePlatformNetworkChanged;
        _engine.OnStatusMessage -= _statusMessageHandler;
        OctopusEngine.OnCertificateRevoked -= _certRevokedHandler;
        _engine.OnTrafficUpdated -= _trafficUpdatedHandler;
        _engine.OnMtuUpdated -= _mtuUpdatedHandler;
        _engine.OnConnectionDropped -= HandleEngineDrop;
        _engine.OnDeadConnectionDetected -= HandleDeadConnection;
        _engine.OnUplinkLost -= HandleUplinkLost;
        _engine.OnUplinkRestored -= HandleUplinkRestored;
        _engine.OnTunnelIpChanged -= HandleTunnelIpChanged;
        _engine.EnsureExternalHostRoutesAsync = null;

        CancelRoamingUnsafe();
        try
        {
            _ = StopVpnAsync().Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignored
        }

        _vpnGate.Dispose();
    }

    public AppVpnState CurrentState { get; private set; } = AppVpnState.Disconnected;

    public bool IsRunning => CurrentState == AppVpnState.Connected;

    public event Action<AppVpnState>? OnStateChanged;
    public event Action<string>? OnLogUpdated;
    public event Action<string>? OnErrorOccurred;
    public event Action<AppTrafficStats>? OnTrafficUpdated = delegate { };
    public event Action<string>? OnForceLogoutRequested;

    public string? DetectConflictingVpn() => _adapter.DetectConflictingVpn();

    public Task<string?> RunNetworkPreflightAsync() => _adapter.RunNetworkPreflightAsync();

    public Task StartVpnAsync(string serverIp, int serverPort) =>
        StartVpnAsync(serverIp, serverPort, null);

    //единый конвейер запуска vpn-подключения через круговое кольцо протоколов и серверов
    public Task StartVpnAsync(string serverIp, int serverPort, IReadOnlyList<VpnServerDto>? fallbackServers) =>
        StartVpnCoreAsync(serverIp, serverPort, fallbackServers, true, null);

    //единая остановка туннеля и полная очистка маршрутов/адаптера
    public async Task StopVpnAsync()
    {
        _ = Interlocked.Exchange(ref _isExplicitlyStopped, 1);
        _ = Interlocked.Increment(ref _sessionEpoch);
        CancelRoamingUnsafe();
        await CancelAndDisposeSessionCtsAsync().ConfigureAwait(false);

        if (CurrentState == AppVpnState.Disconnected)
        {
            await _adapter.ShutdownTunnelAsync(GetRoutedIpsSnapshot(), _currentAssignedIp, false).ConfigureAwait(false);
            await _engine.DisposeAsync().ConfigureAwait(false);
            return;
        }

        UpdateState(AppVpnState.Disconnecting);

        var lockTaken = false;
        try
        {
            lockTaken = await _vpnGate.WaitAsync(5000).ConfigureAwait(false);
            if (!lockTaken)
            {
                AppLogger.LogWarning("[WARN] StopVpnAsync timed out waiting for gate lock. Forcing engine teardown...");
                try
                {
                    await _engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(800)).ConfigureAwait(false);
                }
                catch
                {
                    // ignored
                }
            }

            OnLogUpdated?.Invoke(VpnStatusMessages.DisconnectingVpn);

            var ipsToClean = GetRoutedIpsSnapshot();
            if (ipsToClean.Length == 0 && !string.IsNullOrWhiteSpace(_currentServerIp))
            {
                ipsToClean = [_currentServerIp];
            }

            try
            {
                await _adapter.ShutdownTunnelAsync(ipsToClean, _currentAssignedIp, false).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLogger.LogError($"[STOP ADAPTER ERROR] {ex.Message}", ex);
            }

            try
            {
                await _engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(1200)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLogger.LogError($"[STOP ENGINE ERROR] {ex.Message}", ex);
            }

            _currentAssignedIp = "";
            _currentAssignedIpV6 = null;
            lock (_routesLock)
            {
                _serverIpsToRoute.Clear();
            }

            AppLogger.Log("[SYSTEM] VPN cleanup complete.");
            OnLogUpdated?.Invoke(VpnStatusMessages.VpnDisconnectedSystem);
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
                catch
                {
                    // ignored
                }
            }
        }
    }

    //фоновый перезапуск после обрыва/роуминга, не нарушающий ручную остановку пользователем
    private Task StartBackgroundReconnectAsync(string serverIp, int serverPort, IReadOnlyList<VpnServerDto>? fallbackServers,
        long callerEpoch) =>
        StartVpnCoreAsync(serverIp, serverPort, fallbackServers, false, callerEpoch);

    private async Task StartVpnCoreAsync(string serverIp, int serverPort, IReadOnlyList<VpnServerDto>? fallbackServers,
        bool isUserInitiated, long? callerEpoch)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (!isUserInitiated && (Volatile.Read(ref _isExplicitlyStopped) != 0 || callerEpoch is { } preEp && IsSessionStale(preEp)))
        {
            return;
        }

        await _vpnGate.WaitAsync().ConfigureAwait(false);
        if (!isUserInitiated && (Volatile.Read(ref _isExplicitlyStopped) != 0 || Volatile.Read(ref _disposed) != 0 ||
                                 callerEpoch is { } lockEp && IsSessionStale(lockEp)))
        {
            try
            {
                _ = _vpnGate.Release();
            }
            catch (ObjectDisposedException)
            {
            }

            return;
        }

        var epoch = Interlocked.Increment(ref _sessionEpoch);
        try
        {
            _ = Interlocked.Exchange(ref _isExplicitlyStopped, 0);
            CancelRoamingUnsafe();

            _fallbackServers = fallbackServers is not null ? [.. fallbackServers] : [];
            if (!string.IsNullOrEmpty(serverIp) && _fallbackServers.FindIndex(s => s.Ip == serverIp) < 0)
            {
                _fallbackServers.Insert(0, new VpnServerDto(serverIp, serverPort, "", true, 0));
            }

            UpdateState(AppVpnState.Connecting);
            OnLogUpdated?.Invoke(VpnStatusMessages.CleaningOldSettings);

            var activeCts = new CancellationTokenSource();
            var oldSessionCts = Interlocked.Exchange(ref _sessionCts, activeCts);
            if (oldSessionCts is not null)
            {
                using (oldSessionCts)
                {
                    try
                    {
                        await oldSessionCts.CancelAsync().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        //игнорируем уже освобожденный токен
                    }
                }
            }

            var ct = activeCts.Token;

            await _adapter.CleanupBeforeConnectAsync(ct).ConfigureAwait(false);
            if (IsSessionStale(epoch))
            {
                return;
            }

            var targetIp = serverIp;
            if (Uri.CheckHostName(serverIp) == UriHostNameType.Dns)
            {
                try
                {
                    var resolved = await ApiService.ResolveHostAntiPoisonAsync(serverIp, ct).ConfigureAwait(false);
                    if (resolved.AddressFamily == AddressFamily.InterNetwork)
                    {
                        targetIp = resolved.ToString();
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    AppLogger.LogWarning($"[DNS ERROR] Could not resolve {serverIp}: {ex.Message}");
                }
            }

            _currentServerIp = targetIp;

            if (!await _adapter.PreparePermissionsAndStartServiceAsync(ct).ConfigureAwait(false))
            {
                UpdateState(AppVpnState.Disconnected);
                ReportError("VPN разрешение не выдано", false);
                return;
            }

            OnLogUpdated?.Invoke(VpnStatusMessages.DomainRoute(targetIp));

            SyncPipelinePreferences();
            _engine.ProtectTransportSockets(_adapter.BindPhysicalSocket);
            _engine.EnsureExternalHostRoutesAsync = TrackAndPrepareBypassHostsAsync;

            var serversToTry = _fallbackServers.Count > 0
                ? _fallbackServers
                : [new VpnServerDto(serverIp, serverPort, "", true, 0)];

            var preferredProtocol = ProtocolPipelineRegistry.GetPreferredProtocol();
            var forceWebSocket = PlatformServices.Preferences.Get(AppPreferenceKeys.ForceWebSocket, false);
            var ring = SniEngine.BuildProtocolRotationRing(serversToTry, serverIp, serverPort, null, forceWebSocket, preferredProtocol);
            if (!string.Equals(preferredProtocol, ProtocolPipelineRegistry.AutoProtocolId, StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Log($"[VPN-ORCHESTRATOR] Manual protocol preference active: {preferredProtocol} ({ring.Count} route step(s))");
            }
            if (ring.Count == 0)
            {
                throw new InvalidOperationException("Нет доступных маршрутов или протоколов для подключения.");
            }

            lock (_routesLock)
            {
                _serverIpsToRoute.Clear();
            }

            var activeStep = await _engine.ConnectRotationRingAsync(
                ring,
                async (step, stepToken) =>
                {
                    stepToken.ThrowIfCancellationRequested();
                    if (IsSessionStale(epoch))
                    {
                        throw new OperationCanceledException("Сессия отменена.");
                    }

                    var resolvedStepIp = IPAddress.TryParse(step.Host, out _)
                        ? step.Host
                        : GetFirstRoutedIpSnapshot() ?? targetIp;
                    _currentServerIp = resolvedStepIp;
                    OnLogUpdated?.Invoke(VpnStatusMessages.ConnectingWithProtocol(step.Host, step.Port, _engine.ActiveProtocolDisplayName));

                    var ipv4 = _engine.AssignedIp;
                    var effectiveIpv6 = ValidateIpv6(_engine.AssignedIpV6);
                    if (!IPAddress.TryParse(ipv4, out _))
                    {
                        throw new InvalidOperationException("Получен некорректный IP-адрес от сервера.");
                    }

                    _currentAssignedIp = ipv4;
                    _currentAssignedIpV6 = effectiveIpv6;
                    await _adapter.ApplyTunnelInterfaceAsync(step, ipv4, effectiveIpv6, SmartMtuEngine.CurrentMtu, stepToken)
                        .ConfigureAwait(false);
                    OnLogUpdated?.Invoke(VpnStatusMessages.CheckingChannelDuplex);
                },
                ct).ConfigureAwait(false);

            if (IsSessionStale(epoch))
            {
                return;
            }

            var curTx = _engine.TotalBytesSent;
            var curRx = _engine.TotalBytesReceived;
            AppLogger.Log(
                $"[VPN-ORCHESTRATOR] Ring connected on {activeStep.Kind} ({activeStep.Host}:{activeStep.Port}), TX={curTx} B, RX={curRx} B, MTU={SmartMtuEngine.CurrentMtu}");
            OnLogUpdated?.Invoke(VpnStatusMessages.ChannelVerifiedSecureDuplex(curTx, curRx));

            _adapter.StartPacketPump(ct);

            UpdateActiveServerMetadata(activeStep.Host, activeStep.ServerLabel, activeStep.IsShield);
            var ipsSnapshot = GetRoutedIpsSnapshot();
            await _adapter.ActivateRoutingAndLeakGuardAsync(ipsSnapshot, _currentAssignedIp, _currentAssignedIpV6, ct)
                .ConfigureAwait(false);

            _engine.ResetTrafficCounters();
            _engine.ArmTrafficWatchdog();
            OnLogUpdated?.Invoke(VpnStatusMessages.ProtectedConnectionActive);
            UpdateState(AppVpnState.Connected);
        }
        catch (OperationCanceledException) when (IsSessionStale(epoch))
        {
            AppLogger.Log("[VPN DISCONNECT] Normal stop/cancellation.");
            UpdateState(AppVpnState.Disconnected);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await CancelAndDisposeSessionCtsAsync().ConfigureAwait(false);
            try
            {
                await _adapter.ShutdownTunnelAsync(GetRoutedIpsSnapshot(), _currentAssignedIp, false).ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }

            await _engine.DisposeAsync().ConfigureAwait(false);
            _currentAssignedIp = "";
            _currentAssignedIpV6 = null;

            if (IsSessionStale(epoch))
            {
                UpdateState(AppVpnState.Disconnected);
                return;
            }

            AppLogger.LogError($"[VPN-ORCHESTRATOR FATAL] Connection pipeline failed: {ex.Message}", ex);
            var activePort = _engine.ConnectedServerPort > 0 ? _engine.ConnectedServerPort : serverPort;
            var errMsg = ex is TimeoutException || ex.InnerException is TimeoutException
                ? $"Таймаут подключения: сервер {_currentServerIp}:{activePort} не ответил на TLS хэндшейк."
                : $"Ошибка подключения: {ex.Message}";
            UpdateState(AppVpnState.Error);
            OnLogUpdated?.Invoke(errMsg);
            DispatchError(errMsg);
        }
        finally
        {
            try
            {
                _ = _vpnGate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    //явная фиксация ошибки со стороны ui или системного диалога (например отказ android vpn permission)
    public void ReportError(string message, bool stopTunnel = true)
    {
        if (stopTunnel)
        {
            _ = Task.Run(StopVpnAsync);
        }

        UpdateState(AppVpnState.Error);
        DispatchError(message);
    }

    //единый быстрый реконнект при смене физической сети (wifi -> lte / роуминг)
    public void TriggerImmediateReconnect()
    {
        if (Volatile.Read(ref _isExplicitlyStopped) != 0 ||
            CurrentState is AppVpnState.Disconnected or AppVpnState.Disconnecting ||
            string.IsNullOrEmpty(_currentServerIp))
        {
            return;
        }

        var epoch = Volatile.Read(ref _sessionEpoch);
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

        UpdateState(AppVpnState.Reconnecting);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested || IsSessionStale(epoch))
                {
                    return;
                }

                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    if (ct.IsCancellationRequested || IsSessionStale(epoch))
                    {
                        return;
                    }

                    try
                    {
                        AppLogger.Log($"[NETWORK ROAMING] Fast reconnect attempt #{attempt}...");
                        SyncPipelinePreferences();
                        await _engine.ReconnectAsync(GetActiveSessionToken()).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        await SyncAssignedIpWithAdapterAsync(epoch, ct).ConfigureAwait(false);
                        var verified = await _engine.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(5000), true, ct).ConfigureAwait(false);
                        if (ct.IsCancellationRequested || IsSessionStale(epoch))
                        {
                            return;
                        }

                        if (!verified)
                        {
                            continue;
                        }

                        _engine.ResetTrafficCounters();
                        _engine.ArmTrafficWatchdog();
                        UpdateState(AppVpnState.Connected);
                        AppLogger.Log("[NETWORK ROAMING] Connected to new network interface seamlessly!");
                        return;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                    {
                        AppLogger.LogWarning($"[NETWORK ROAMING ATTEMPT #{attempt} FAILED] {ex.Message}");
                        await Task.Delay(800, ct).ConfigureAwait(false);
                    }
                }

                if (!ct.IsCancellationRequested && !IsSessionStale(epoch) && Volatile.Read(ref _isExplicitlyStopped) == 0)
                {
                    AppLogger.Log("[NETWORK ROAMING] Fast reconnect attempts exhausted. Initiating server failover...");
                    var port = _engine.ConnectedServerPort > 0 ? _engine.ConnectedServerPort : 443;
                    await StartBackgroundReconnectAsync(_currentServerIp, port, _fallbackServers, epoch).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLogger.LogError($"[NETWORK ROAMING RECONNECT] {ex.Message}", ex);
                if (!IsSessionStale(epoch))
                {
                    ReportError("Не удалось восстановить подключение после смены сети.");
                }
            }
        }, ct);
    }

    private void HandlePlatformNetworkChanged()
    {
        if (Volatile.Read(ref _isExplicitlyStopped) != 0)
        {
            return;
        }

        if (CurrentState is AppVpnState.Connected or AppVpnState.Reconnecting)
        {
            TriggerImmediateReconnect();
        }
    }

    private void HandleMtuUpdated(int optimalMtu)
    {
        if (Volatile.Read(ref _isExplicitlyStopped) != 0)
        {
            return;
        }

        _adapter.UpdateAdapterMtu(optimalMtu);
    }

    private void HandleTunnelIpChanged()
    {
        var epoch = Volatile.Read(ref _sessionEpoch);
        if (IsSessionStale(epoch))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await SyncAssignedIpWithAdapterAsync(epoch, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLogger.LogWarning($"[VPN-ORCHESTRATOR] Tunnel IP sync after rotation failed: {ex.Message}");
            }
        });
    }

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

    private void HandleUplinkLost()
    {
        var epoch = Volatile.Read(ref _sessionEpoch);
        if (!IsRunning || IsSessionStale(epoch))
        {
            return;
        }

        AppLogger.LogWarning("[VPN-ORCHESTRATOR] Physical uplink lost during active session. Suspending tunnel routes...");
        OnLogUpdated?.Invoke("Физическая сеть отключена. Маршруты сброшены.");
        UpdateState(AppVpnState.Reconnecting);
        _ = Task.Run(async () =>
        {
            try
            {
                await _adapter.HandlePhysicalUplinkLostAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLogger.LogWarning($"[VPN-ORCHESTRATOR] Uplink-lost cleanup warning: {ex.Message}");
            }
        });
    }

    private void HandleUplinkRestored()
    {
        var epoch = Volatile.Read(ref _sessionEpoch);
        if (CurrentState != AppVpnState.Reconnecting || IsSessionStale(epoch))
        {
            return;
        }

        AppLogger.Log("[VPN-ORCHESTRATOR] Physical uplink restored. Resuming connection...");
        OnLogUpdated?.Invoke("Сеть восстановлена. Подключение к узлу...");
        _ = Task.Run(async () =>
        {
            if (IsSessionStale(epoch))
            {
                return;
            }

            if (PlatformServices.Preferences.Get(AppPreferenceKeys.AutoReconnect, true))
            {
                var targetPort = _engine.ConnectedServerPort > 0 ? _engine.ConnectedServerPort : 443;
                await StartBackgroundReconnectAsync(_currentServerIp, targetPort, _fallbackServers, epoch).ConfigureAwait(false);
            }
            else
            {
                await StopVpnAsync().ConfigureAwait(false);
            }
        });
    }

    //единая обработка мертвого соединения (0 rx / заморозка потока) через круговую ротацию
    private void HandleDeadConnection()
    {
        var epoch = Volatile.Read(ref _sessionEpoch);
        if (!IsRunning || IsSessionStale(epoch))
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
                if (!OctopusEngine.IsUplinkAvailable)
                {
                    AppLogger.LogWarning("[DEAD CONNECTION] Physical network is down. Suspending failover loops.");
                    OnLogUpdated?.Invoke("Физическая сеть отключена. Ожидание подключения...");
                    UpdateState(AppVpnState.Reconnecting);
                    await _adapter.HandlePhysicalUplinkLostAsync().ConfigureAwait(false);
                    return;
                }

                AppLogger.LogWarning(
                    $"[DEAD CONNECTION] Packet blackhole detected. Initiating circular protocol recovery from {_currentServerIp}:{_engine.ConnectedServerPort}...");
                OnLogUpdated?.Invoke(VpnStatusMessages.PacketLossDetected);
                UpdateState(AppVpnState.Reconnecting);

                try
                {
                    if (await _engine.TryRotateProtocolAsync("DeadConnectionRecovery").ConfigureAwait(false))
                    {
                        if (IsSessionStale(epoch))
                        {
                            return;
                        }

                        var activeIp = _engine.ConnectedServerIp;
                        if (IPAddress.TryParse(activeIp, out _))
                        {
                            _currentServerIp = activeIp;
                        }

                        await SyncAssignedIpWithAdapterAsync(epoch, CancellationToken.None).ConfigureAwait(false);
                        _engine.ResetTrafficCounters();
                        _engine.ArmTrafficWatchdog();
                        UpdateState(AppVpnState.Connected);
                        OnLogUpdated?.Invoke(VpnStatusMessages.ProtocolSwitched(_engine.ActiveProtocolDisplayName));
                        return;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    AppLogger.LogWarning($"[DEAD CONNECTION] In-place protocol rotation failed: {ex.Message}");
                }

                for (var attempt = 1; attempt <= 2; attempt++)
                {
                    if (IsSessionStale(epoch) || !OctopusEngine.IsUplinkAvailable)
                    {
                        return;
                    }

                    try
                    {
                        SyncPipelinePreferences();
                        await _engine.ReconnectAsync(GetActiveSessionToken()).ConfigureAwait(false);
                        if (IsSessionStale(epoch))
                        {
                            return;
                        }

                        await SyncAssignedIpWithAdapterAsync(epoch, CancellationToken.None).ConfigureAwait(false);
                        var verified = await _engine.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(5000), true).ConfigureAwait(false);
                        if (IsSessionStale(epoch))
                        {
                            return;
                        }

                        if (verified)
                        {
                            var activeIp = _engine.ConnectedServerIp;
                            if (IPAddress.TryParse(activeIp, out _))
                            {
                                _currentServerIp = activeIp;
                            }

                            _engine.ResetTrafficCounters();
                            _engine.ArmTrafficWatchdog();
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

                if (IsSessionStale(epoch))
                {
                    return;
                }

                await StopVpnAsync().ConfigureAwait(false);
                ReportError("Ни один из протоколов не получил ответного трафика (0 RX).", false);
            }
            finally
            {
                Volatile.Write(ref _isHandlingDeadConnection, 0);
            }
        });
    }

    //единая обработка обрыва соединения с exponential backoff и поддержкой kill switch
    private void HandleEngineDrop()
    {
        var epoch = Volatile.Read(ref _sessionEpoch);
        if (!IsRunning || IsSessionStale(epoch))
        {
            return;
        }

        var autoReconnect = PlatformServices.Preferences.Get(AppPreferenceKeys.AutoReconnect, true);
        var killSwitch = PlatformServices.Preferences.Get(AppPreferenceKeys.KillSwitch, false);

        if (!autoReconnect)
        {
            _ = Task.Run(async () =>
            {
                if (!killSwitch || !_adapter.SupportsKillSwitch)
                {
                    await StopVpnAsync().ConfigureAwait(false);
                }

                ReportError("Связь с сервером потеряна.", false);
            });
            return;
        }

        if (Interlocked.CompareExchange(ref _isHandlingDrop, 1, 0) != 0)
        {
            return;
        }

        CancelRoamingUnsafe();
        UpdateState(AppVpnState.Reconnecting);

        _ = Task.Run(async () =>
        {
            try
            {
                if (!OctopusEngine.IsUplinkAvailable)
                {
                    AppLogger.LogWarning("[ENGINE DROP] Physical network is down. Dropping routes without retry loops.");
                    OnLogUpdated?.Invoke("Физическая сеть отключена. Ожидание подключения...");
                    UpdateState(AppVpnState.Reconnecting);
                    await _adapter.HandlePhysicalUplinkLostAsync().ConfigureAwait(false);
                    return;
                }

                for (var attempt = 0; attempt < 5; attempt++)
                {
                    var backoffMs = 1000 + attempt * 1000;
                    await Task.Delay(backoffMs).ConfigureAwait(false);
                    if (IsSessionStale(epoch) || !OctopusEngine.IsUplinkAvailable)
                    {
                        return;
                    }

                    try
                    {
                        SyncPipelinePreferences();
                        await _engine.ReconnectAsync(GetActiveSessionToken()).ConfigureAwait(false);
                        if (IsSessionStale(epoch))
                        {
                            return;
                        }

                        var activeIp = _engine.ConnectedServerIp;
                        if (IPAddress.TryParse(activeIp, out _))
                        {
                            _currentServerIp = activeIp;
                        }

                        await SyncAssignedIpWithAdapterAsync(epoch, CancellationToken.None).ConfigureAwait(false);
                        var verified = await _engine.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(4500), true).ConfigureAwait(false);
                        if (IsSessionStale(epoch))
                        {
                            return;
                        }

                        if (verified)
                        {
                            _engine.ResetTrafficCounters();
                            _engine.ArmTrafficWatchdog();
                            UpdateState(AppVpnState.Connected);
                            OnLogUpdated?.Invoke(VpnStatusMessages.ConnectionRestored);
                            return;
                        }

                        await _engine.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        AppLogger.LogWarning($"[RECONNECT] Circular drop recovery attempt {attempt + 1} failed: {ex.Message}");
                        await _engine.DisposeAsync().ConfigureAwait(false);
                    }
                }

                if (IsSessionStale(epoch))
                {
                    return;
                }

                if (killSwitch && _adapter.SupportsKillSwitch)
                {
                    ReportError("Не удалось восстановить связь. Kill Switch блокирует утечку IP. Нажмите «Стоп» для отключения.", false);
                }
                else
                {
                    await StopVpnAsync().ConfigureAwait(false);
                    ReportError("Связь с сервером потеряна. Не удалось восстановить подключение.", false);
                }
            }
            finally
            {
                Volatile.Write(ref _isHandlingDrop, 0);
            }
        });
    }

    private async Task TrackAndPrepareBypassHostsAsync(string[]? ips, CancellationToken ct)
    {
        if (ips is null || ips.Length == 0)
        {
            return;
        }

        lock (_routesLock)
        {
            foreach (var ip in ips)
            {
                if (IPAddress.TryParse(ip, out var parsed) &&
                    parsed.AddressFamily == AddressFamily.InterNetwork &&
                    !_serverIpsToRoute.Contains(ip))
                {
                    _serverIpsToRoute.Add(ip);
                }
            }
        }

        await _adapter.PrepareBypassHostsAsync(ips, ct).ConfigureAwait(false);
    }

    private async Task SyncAssignedIpWithAdapterAsync(long epoch, CancellationToken ct)
    {
        if (IsSessionStale(epoch))
        {
            return;
        }

        UpdateActiveServerMetadata(_engine.ConnectedServerIp, null, false);

        var newIpv4 = _engine.AssignedIp;
        var newIpv6 = ValidateIpv6(_engine.AssignedIpV6);
        if (string.IsNullOrWhiteSpace(newIpv4) || !IPAddress.TryParse(newIpv4, out _))
        {
            return;
        }

        if (!string.Equals(_currentAssignedIp, newIpv4, StringComparison.Ordinal) ||
            !string.Equals(_currentAssignedIpV6, newIpv6, StringComparison.Ordinal))
        {
            AppLogger.Log($"[VPN-ORCHESTRATOR] Assigned tunnel IP changed ({_currentAssignedIp} -> {newIpv4}). Syncing OS adapter...");
            _currentAssignedIp = newIpv4;
            _currentAssignedIpV6 = newIpv6;
            await _adapter.SyncTunnelIpAndRoutesAsync(GetRoutedIpsSnapshot(), newIpv4, newIpv6, SmartMtuEngine.CurrentMtu, ct)
                .ConfigureAwait(false);
        }
        else
        {
            _adapter.UpdateAdapterMtu(SmartMtuEngine.CurrentMtu);
        }
    }

    private void SyncPipelinePreferences() =>
        _engine.Pipeline.BlockIpv6Traffic = PlatformServices.Preferences.Get(AppPreferenceKeys.BlockIpv6, false);

    //разрешение реальной локации и флага активного узла vpn для синхронизации часового пояса
    private void UpdateActiveServerMetadata(string? activeHost, string? stepLabel, bool isShield)
    {
        var servers = _fallbackServers;
        VpnServerDto? matched = null;

        if (!string.IsNullOrWhiteSpace(activeHost))
        {
            matched = servers.Find(s => string.Equals(s.Ip, activeHost, StringComparison.OrdinalIgnoreCase));
        }

        if (matched is not null && (!string.IsNullOrWhiteSpace(matched.Location) || !string.IsNullOrWhiteSpace(matched.Flag)))
        {
            _engine.SetActiveServerLocation(matched.Location, matched.Flag);
            return;
        }

        if (!isShield &&
            !string.IsNullOrWhiteSpace(stepLabel) &&
            !stepLabel.StartsWith("Cloudflare", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(stepLabel, "Direct Origin", StringComparison.OrdinalIgnoreCase))
        {
            _engine.SetActiveServerLocation(stepLabel, matched?.Flag);
            return;
        }

        var primary = servers.Find(s => !string.IsNullOrWhiteSpace(s.Location) || !string.IsNullOrWhiteSpace(s.Flag));
        if (primary is not null)
        {
            _engine.SetActiveServerLocation(primary.Location, primary.Flag);
        }
    }

    private bool IsSessionStale(long epoch) =>
        Volatile.Read(ref _isExplicitlyStopped) != 0 ||
        Volatile.Read(ref _disposed) != 0 ||
        Volatile.Read(ref _sessionEpoch) != epoch;

    private string[] GetRoutedIpsSnapshot()
    {
        lock (_routesLock)
        {
            return [.. _serverIpsToRoute];
        }
    }

    private string? GetFirstRoutedIpSnapshot()
    {
        lock (_routesLock)
        {
            return _serverIpsToRoute.Count > 0 ? _serverIpsToRoute[0] : null;
        }
    }

    private static string? ValidateIpv6(string? ip6) =>
        !string.IsNullOrWhiteSpace(ip6) &&
        IPAddress.TryParse(ip6, out var parsed) &&
        parsed.AddressFamily == AddressFamily.InterNetworkV6
            ? ip6
            : null;

    private void CancelRoamingUnsafe()
    {
        lock (_roamingLock)
        {
            try
            {
                _roamingCts?.Cancel();
                _roamingCts?.Dispose();
            }
            catch
            {
                // ignored
            }
            finally
            {
                _roamingCts = null;
            }
        }
    }

    private async Task CancelAndDisposeSessionCtsAsync()
    {
        var cts = Interlocked.Exchange(ref _sessionCts, null);
        if (cts is not null)
        {
            using (cts)
            {
                try
                {
                    await cts.CancelAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    //игнорируем уже освобожденный токен
                }
            }
        }
    }

    private CancellationToken GetActiveSessionToken()
    {
        var cts = Volatile.Read(ref _sessionCts);
        return cts?.Token ?? CancellationToken.None;
    }

    private void UpdateState(AppVpnState newState)
    {
        if (CurrentState == newState)
        {
            return;
        }

        CurrentState = newState;
        PlatformServices.MainThread.BeginInvokeOnMainThread(() => OnStateChanged?.Invoke(CurrentState));
    }

    private void DispatchError(string message) =>
        PlatformServices.MainThread.BeginInvokeOnMainThread(() => OnErrorOccurred?.Invoke(message));
}
