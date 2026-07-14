namespace obxodka.Services;

public interface IAppUpdaterService
{
    public Task CheckForUpdatesAsync(bool manualCheck = false);
}
