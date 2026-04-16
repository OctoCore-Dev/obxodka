namespace obxodka.Core;
internal enum AppVpnState
{
    Disconnected,
    Connecting,
    Connected,
    Error,
    Reconnecting,
    Disconnecting
}
internal sealed class AppTrafficStats
{
    public long DownloadSpeedBps { get; set; }
    public long UploadSpeedBps { get; set; }
}
internal interface IVpnService
{
    AppVpnState CurrentState { get; }
    bool IsRunning { get; }
    event Action<AppVpnState>? OnStateChanged;
    event Action<string>? OnErrorOccurred;
    event Action<AppTrafficStats>? OnTrafficUpdated;
    Task StartVpn(string vlessLink, bool useAdblock = false);
    void StopVpn();
}