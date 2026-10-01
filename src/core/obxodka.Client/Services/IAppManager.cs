namespace obxodka.Services;

public interface IAppManager
{
    public Task<List<AppInfoItem>> GetInstalledAppsAsync();
    public List<string> GetBypassedPackages();
    public void SaveBypassedPackages(List<string> packages);
}
