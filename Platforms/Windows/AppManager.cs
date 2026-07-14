namespace obxodka.Platforms.Windows;

public class AppManager : IAppManager
{
    public Task<List<AppInfoItem>> GetInstalledAppsAsync() => Task.FromResult(new List<AppInfoItem>());

    public List<string> GetBypassedPackages() => [];

    public void SaveBypassedPackages(List<string> packages)
    {
    }
}
