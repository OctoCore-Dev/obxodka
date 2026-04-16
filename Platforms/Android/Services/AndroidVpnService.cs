namespace obxodka.Platforms.Android.Services;
internal sealed class AndroidVpnService : IVpnService
{
    public AppVpnState CurrentState { get; private set; } = AppVpnState.Disconnected;
    public bool IsRunning => ObxodkaVpnService.IsVpnRunning;
    public event Action<AppVpnState>? OnStateChanged;
    public event Action<string>? OnErrorOccurred;
    public event Action<AppTrafficStats>? OnTrafficUpdated;
    public AndroidVpnService()
    {
        ObxodkaVpnService.NativeStateChanged += (isRunning) =>
        {
            ChangeState(isRunning ? AppVpnState.Connected : AppVpnState.Disconnected);
        };
    }
    private void ChangeState(AppVpnState newState)
    {
        if (CurrentState == newState) return;
        CurrentState = newState;
        MainThread.BeginInvokeOnMainThread(() => OnStateChanged?.Invoke(CurrentState));
    }
    public async Task StartVpn(string vlessLink, bool useAdblock = false)
    {
        if (IsRunning) return;
        try
        {
            ChangeState(AppVpnState.Connecting);
            string linkPath = Path.Combine(FileSystem.AppDataDirectory, "current_vless.txt");
            File.WriteAllText(linkPath, vlessLink);
            obxodka.MainActivity.StartVpnService();
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(500);
                if (ObxodkaVpnService.IsVpnRunning) return;
            }
            ChangeState(AppVpnState.Error);
            OnErrorOccurred?.Invoke("Таймаут запуска VPN.");
        }
        catch (Exception ex)
        {
            ChangeState(AppVpnState.Error);
            OnErrorOccurred?.Invoke($"Сбой запуска: {ex.Message}");
        }
    }
    public void StopVpn()
    {
        ChangeState(AppVpnState.Disconnecting);
        ObxodkaVpnService.ForceStop();
    }
}