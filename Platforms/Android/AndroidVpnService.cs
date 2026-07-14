using Android.Content;

namespace obxodka.Platforms.Android;

[SupportedOSPlatform("android30.0")]
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
    private string _currentServerIp = "";
    private int _currentServerPort = 443;
    private bool _isExplicitlyStopped;
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
        _currentServerIp = serverIp;
        _currentServerPort = serverPort;
        _isExplicitlyStopped = false;
        var intent = global::Android.Net.VpnService.Prepare(Platform.AppContext);
        if (intent != null)
        {
            var granted = await MainActivity.RequestVpnPermissionAsync(intent);
            if (!granted)
            {
                SetError("VPN разрешение не выдано");
                return;
            }
        }
        ChangeState(AppVpnState.Connecting);
        await OctopusEngine.Current.ConnectAsync(serverIp, serverPort);
        MainActivity.StartVpnService();
    }
    public async Task StopVpnAsync()
    {
        _isExplicitlyStopped = true;
        var context = Platform.AppContext;
        var intent = new Intent(context, typeof(OctopusVpnService))
            .SetAction("STOP");
        _ = context.StartService(intent);
        await OctopusEngine.Current.DisposeAsync();
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
