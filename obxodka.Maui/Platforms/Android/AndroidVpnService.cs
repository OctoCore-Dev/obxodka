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

    private AndroidVpnService()
    {
        OctopusEngine.OnCertificateRevoked += (msg) => OnForceLogoutRequested?.Invoke(msg);
        OctopusEngine.Current.OnDeadConnectionDetected -= HandleDeadConnection;
        OctopusEngine.Current.OnDeadConnectionDetected += HandleDeadConnection;
    }

    private void HandleDeadConnection()
    {
        if (IsRunning && !_isExplicitlyStopped)
        {
            _ = Task.Run(async () =>
            {
                await StopVpnAsync();
                SetError("Сервер отключил соединение.");
            });
        }
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

            ChangeState(AppVpnState.Reconnecting);
            _ = Task.Run(async () =>
            {
                try
                {
                    for (var i = 0; i < 10; i++)
                    {
                        if (i > 0)
                        {
                            await Task.Delay(1000);
                        }

                        if (_isExplicitlyStopped)
                        {
                            return;
                        }

                        try
                        {
                            await OctopusEngine.Current.ReconnectAsync(_currentServerIp, _currentServerPort);
                            OctopusVpnService.Instance?.EstablishTun();
                            ChangeState(AppVpnState.Connected);
                            return;
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

                for (var attempt = 1; attempt <= 5; attempt++)
                {
                    if (ct.IsCancellationRequested || _isExplicitlyStopped)
                    {
                        return;
                    }

                    try
                    {
                        Debug.WriteLine($"[NETWORK ROAMING] Fast reconnect attempt #{attempt}...");
                        await OctopusEngine.Current.ReconnectAsync(_currentServerIp, _currentServerPort);
                        OctopusVpnService.Instance?.EstablishTun();
                        ChangeState(AppVpnState.Connected);
                        Debug.WriteLine("[NETWORK ROAMING] Connected to new network interface seamlessly!");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[NETWORK ROAMING ATTEMPT #{attempt} FAILED] {ex.Message}");
                        await Task.Delay(500, ct);
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

    public async Task StartVpnAsync(string serverIp, int serverPort)
    {
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
                SetError("VPN разрешение не выдано");
                return;
            }
        }

        OnLogUpdated?.Invoke($"[DOMAINS] Traffic will route via domain: {originalHost}");
        ChangeState(AppVpnState.Connecting);

        try
        {
            MainActivity.StartVpnService();
            for (var i = 0; i < 30 && OctopusVpnService.Instance is null; i++)
            {
                await Task.Delay(50);
            }

            await OctopusEngine.Current.ConnectAsync(targetIp, serverPort);
            OctopusVpnService.Instance?.EstablishTun();
        }
        catch (Exception ex)
        {
            try
            {
                OctopusVpnService.Instance?.StopNativeVpn();
            }
            catch { }

            if (ex is OperationCanceledException ||
                ex.InnerException is OperationCanceledException ||
                ex.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase) ||
                _isExplicitlyStopped)
            {
                Debug.WriteLine($"[VPN DISCONNECT] Normal stop/cancellation: {ex.Message}");
                ChangeState(AppVpnState.Disconnected);
                return;
            }

            SetError($"Ошибка подключения: {ex.Message}");
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
