using obxodka.Shared.Config;
using Uri = System.Uri;

namespace obxodka.Platforms.Android;

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
    private int _currentServerPort = 443;
    private bool _isExplicitlyStopped;
    private List<VpnServerDto> _fallbackServers = [];
    private int _currentServerIndex;
    private int _isHandlingDeadConnection;

    private AndroidVpnService()
    {
        OctopusEngine.OnCertificateRevoked += (msg) => OnForceLogoutRequested?.Invoke(msg);
        OctopusEngine.Current.OnDeadConnectionDetected -= HandleDeadConnection;
        OctopusEngine.Current.OnDeadConnectionDetected += HandleDeadConnection;
    }

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
                Debug.WriteLine("[DEAD CONNECTION] Packet blackhole detected. Initiating smart failover...");
                OnLogUpdated?.Invoke("[SMART CONNECT] Обнаружена блокировка передачи пакетов. Автопереключение...");
                ChangeState(AppVpnState.Reconnecting);

                try
                {
                    await OctopusEngine.Current.ReconnectAsync(_currentServerIp, _currentServerPort);
                    _ = OctopusVpnService.Instance?.EstablishTun();
                    var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(5000));
                    if (verified || OctopusEngine.Current.IsConnected)
                    {
                        ChangeState(AppVpnState.Connected);
                        OnLogUpdated?.Invoke("[SMART CONNECT] Соединение восстановлено через TCP/TLS!");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SMART CONNECT] Reconnect failed on {_currentServerIp}: {ex.Message}");
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

                        _currentServerIndex = nextIdx;
                        _currentServerIp = nextServer.Ip;
                        _currentServerPort = nextServer.Port > 0 ? nextServer.Port : 443;
                        if (!string.IsNullOrWhiteSpace(nextServer.CertHash))
                        {
                            OctopusEngine.DynamicSslPublicKeyHash = nextServer.CertHash;
                        }

                        OnLogUpdated?.Invoke($"[SMART CONNECT] Переключение на резервный сервер: {_currentServerIp}...");
                        try
                        {
                            await OctopusEngine.Current.ReconnectAsync(_currentServerIp, _currentServerPort);
                            _ = OctopusVpnService.Instance?.EstablishTun();
                            var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(4000));
                            if (verified || OctopusEngine.Current.IsConnected)
                            {
                                ChangeState(AppVpnState.Connected);
                                OnLogUpdated?.Invoke("[SMART CONNECT] Подключение успешно переведено на новый сервер!");
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[SMART CONNECT] Failover to {_currentServerIp} failed: {ex.Message}");
                        }
                    }
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

    private int _isHandlingDrop;

    public void HandleEngineDrop()
    {
        var autoReconnect = Preferences.Get("AutoReconnect", true);
        var killSwitch = Preferences.Get("KillSwitch", false);

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

            _roamingCts?.Cancel();

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
                            await Task.Delay(backoffMs);
                        }

                        if (_isExplicitlyStopped)
                        {
                            return;
                        }

                        try
                        {
                            await OctopusEngine.Current.ReconnectAsync(_currentServerIp, _currentServerPort);
                            _ = OctopusVpnService.Instance?.EstablishTun();
                            var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(4000));
                            if (verified || OctopusEngine.Current.IsConnected)
                            {
                                ChangeState(AppVpnState.Connected);
                                return;
                            }
                        }
                        catch { }
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

#pragma warning disable CA1001
    private CancellationTokenSource? _roamingCts;
#pragma warning restore CA1001

    public void TriggerImmediateReconnect()
    {
        if (_isExplicitlyStopped || string.IsNullOrEmpty(_currentServerIp))
        {
            return;
        }

        _roamingCts?.Cancel();
        _roamingCts?.Dispose();
        _roamingCts = new CancellationTokenSource();
        var ct = _roamingCts.Token;

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
                        Debug.WriteLine($"[NETWORK ROAMING] Fast reconnect attempt #{attempt}...");
                        await OctopusEngine.Current.ReconnectAsync(_currentServerIp, _currentServerPort);
                        _ = OctopusVpnService.Instance?.EstablishTun();
                        var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(4000), ct);
                        if (verified || OctopusEngine.Current.IsConnected)
                        {
                            ChangeState(AppVpnState.Connected);
                            Debug.WriteLine("[NETWORK ROAMING] Connected to new network interface seamlessly!");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[NETWORK ROAMING ATTEMPT #{attempt} FAILED] {ex.Message}");
                        await Task.Delay(1000, ct);
                    }
                }

                if (!ct.IsCancellationRequested && !_isExplicitlyStopped)
                {
                    Debug.WriteLine("[NETWORK ROAMING] Fast reconnect attempts exhausted. Initiating server failover...");
                    try
                    {
                        await StartVpnAsync(_currentServerIp, _currentServerPort, _fallbackServers);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[NETWORK ROAMING FULL RECOVERY ERROR] {ex.Message}");
                        await StopVpnAsync();
                        SetError("Не удалось восстановить подключение после смены сети.");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NETWORK ROAMING RECONNECT] {ex.Message}");
            }
        }, ct);
    }

    public Task StartVpnAsync(string serverIp, int serverPort) =>
        StartVpnAsync(serverIp, serverPort, null);

    public async Task StartVpnAsync(string serverIp, int serverPort, IReadOnlyList<VpnServerDto>? fallbackServers)
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
                var addrs = await Dns.GetHostAddressesAsync(serverIp);
                if (addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) is { } ipv4)
                {
                    targetIp = ipv4.ToString();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DNS ERROR] Could not resolve {serverIp}: {ex.Message}");
            }
        }

        var originalHost = serverIp;
        if (Uri.CheckHostName(serverIp) != UriHostNameType.Dns)
        {
            try
            {
                originalHost = new Uri(AppConfig.ApiBaseUrl).Host;
            }
            catch { }
        }

        _currentServerIp = targetIp;
        _currentServerPort = serverPort;
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

        OnLogUpdated?.Invoke($"[DOMAINS] Traffic will route via domain: {originalHost}");

        try
        {
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
                OnLogUpdated?.Invoke("Поиск быстрейшего узла (Happy Eyeballs)...");
                serversToTry = [.. await NetworkDefaults.RankServersByLatencyAsync(serversToTry, 650).ConfigureAwait(false)];
            }

            for (var idx = 0; idx < serversToTry.Count; idx++)
            {
                if (_isExplicitlyStopped)
                {
                    return;
                }

                var s = serversToTry[idx];
                var candidateIp = s.Ip;
                var candidatePort = s.Port > 0 ? s.Port : 443;

                if (Uri.CheckHostName(candidateIp) == UriHostNameType.Dns)
                {
                    try
                    {
                        var addrs = await Dns.GetHostAddressesAsync(candidateIp);
                        if (addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) is { } ipv4)
                        {
                            candidateIp = ipv4.ToString();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[DNS ERROR] Could not resolve candidate {candidateIp}: {ex.Message}");
                        lastEx = ex;
                        continue;
                    }
                }

                if (!IPAddress.TryParse(candidateIp, out _))
                {
                    Debug.WriteLine($"[DNS ERROR] Invalid candidate IP: {candidateIp}. Skipping.");
                    continue;
                }

                _currentServerIp = candidateIp;
                _currentServerPort = candidatePort;
                _currentServerIndex = idx;

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
                            OnLogUpdated?.Invoke($"Повтор подключения ({attempt}/2)...");
                        }

                        OnLogUpdated?.Invoke($"Подключение к серверу {candidateIp}:{candidatePort}...");
                        await OctopusEngine.Current.ConnectAsync(candidateIp, candidatePort);

                        var tunOk = OctopusVpnService.Instance?.EstablishTun() ?? false;
                        if (!tunOk)
                        {
                            throw new InvalidOperationException("Не удалось инициализировать TUN интерфейс.");
                        }

                        OctopusEngine.Current.ResetTrafficCounters();
                        OnLogUpdated?.Invoke($"Проверка соединения с сервером (RX={OctopusEngine.Current.TotalBytesReceived} B)...");
                        var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(5000));
                        if (verified)
                        {
                            OnLogUpdated?.Invoke($"Связь подтверждена (RX={OctopusEngine.Current.TotalBytesReceived} B)! Защищенное соединение установлено.");
                        }

                        if (verified || OctopusEngine.Current.TotalBytesReceived > 0)
                        {
                            OctopusEngine.Current.ResetTrafficCounters();
                            OctopusEngine.Current.ArmTrafficWatchdog();
                            ChangeState(AppVpnState.Connected);
                            connected = true;
                            break;
                        }
                        else
                        {
                            throw new InvalidOperationException("Сервер не отвечает на тестовые пакеты (RX=0).");
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                        Debug.WriteLine($"[VPN CONNECT FAILED FOR {candidateIp}] {ex.Message}");
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
                    OnLogUpdated?.Invoke($"Сервер {candidateIp} недоступен или нет трафика. Пробуем запасной сервер...");
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
                Debug.WriteLine($"[VPN DISCONNECT] Normal stop/cancellation: {ex.Message}");
                ChangeState(AppVpnState.Disconnected);
                return;
            }

            SetError(ex is TimeoutException or OperationCanceledException
                ? $"Таймаут подключения: сервер {_currentServerIp}:{_currentServerPort} не ответил на TLS/gRPC хэндшейк."
                : $"Ошибка подключения: {ex.Message}");
        }
    }

    public async Task StopVpnAsync()
    {
        _isExplicitlyStopped = true;
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

    public void ChangeState(AppVpnState newState)
    {
        if (CurrentState == newState)
        {
            return;
        }

        CurrentState = newState;
        MainThread.BeginInvokeOnMainThread(() => OnStateChanged?.Invoke(CurrentState));
    }

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

    public void Dispose() => _roamingCts?.Dispose();
}
