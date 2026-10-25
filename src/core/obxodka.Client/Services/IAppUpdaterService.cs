namespace obxodka.Services;

public sealed record StoreUpdateInfo(
    bool HasUpdate,
    string CurrentVersion,
    string LatestVersion,
    string StoreName,
    string? StoreUrl = null,
    string? ReleaseNotes = null);

public interface IAppUpdaterService
{
    public Task CheckForUpdatesAsync(bool manualCheck = false);
    public Task<StoreUpdateInfo> CheckVersionAsync();
}
