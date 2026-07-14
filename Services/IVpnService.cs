namespace obxodka.Services;

internal interface IVpnService
{
    public AppVpnState CurrentState { get; }
    public bool IsRunning { get; }
    public event Action<AppVpnState>? OnStateChanged;
    public event Action<string>? OnLogUpdated;
    public event Action<string>? OnErrorOccurred;
    public event Action<AppTrafficStats>? OnTrafficUpdated;
    public Task StartVpnAsync(string serverIp, int serverPort);
    public Task StopVpnAsync();
}
