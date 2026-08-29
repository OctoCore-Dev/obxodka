namespace obxodka.Platforms.Windows;

[SupportedOSPlatform("windows")]
public sealed class AppManager : IAppManager
{
    public Task<List<AppInfoItem>> GetInstalledAppsAsync() =>
        Task.FromResult<List<AppInfoItem>>([]);

    public List<string> GetBypassedPackages() => [];

    public void SaveBypassedPackages(List<string> packages)
    {
    }
}
