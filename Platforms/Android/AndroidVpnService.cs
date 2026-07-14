namespace obxodka.Platforms.Android;

[SupportedOSPlatform("android29.0")]
internal sealed class AndroidVpnService : IVpnService
{
    public static AndroidVpnService Instance { get; } = new();
    public AppVpnState CurrentState { get; private set; } = AppVpnState.Disconnected;
    public bool IsRunning => CurrentState == AppVpnState.Connected;
    public event Action<AppVpnState>? OnStateChanged;
#pragma warning disable CS0067
    public event Action<string>? OnLogUpdated;
#pragma warning restore CS0067
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

    public void HandleEngineDrop()
    {
        if (IsRunning && !_isExplicitlyStopped)
        {
            ChangeState(AppVpnState.Reconnecting);
            _ = Task.Run(async () =>
            {
                for (var i = 0; i < 5; i++)
                {
                    await Task.Delay(2000);
                    if (_isExplicitlyStopped)
                    {
                        return;
                    }
                    try
                    {
                        await OctopusEngine.Current.DisposeAsync();
                        await OctopusEngine.Current.ConnectAsync(_currentServerIp, _currentServerPort);
                        ChangeState(AppVpnState.Connected);
                        return;
                    }
                    catch { }
                }
                await StopVpnAsync();
                SetError("Связь с сервером потеряна. Не удалось восстановить подключение.");
            });
        }
    }
    public async Task StartVpnAsync(string serverIp, int serverPort)
    {
        var targetIp = serverIp;
        if (Uri.CheckHostName(serverIp) == UriHostNameType.Dns)
        {
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(serverIp);
                targetIp = addrs.First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToString();
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
        if (intent != null)
#pragma warning disable CA1416
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
            await OctopusEngine.Current.ConnectAsync(targetIp, serverPort);
            MainActivity.StartVpnService();
        }
        catch (Exception ex)
        {
            SetError($"Ошибка подключения: {ex.Message}");
        }
#pragma warning restore CA1416
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
        ChangeState(AppVpnState.Error);
        MainThread.BeginInvokeOnMainThread(() => OnErrorOccurred?.Invoke(message));
    }
}
