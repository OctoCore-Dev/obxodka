namespace obxodka.Services;

//информация о доступном обновлении программы
public sealed record StoreUpdateInfo(
    bool HasUpdate,
    string CurrentVersion,
    string LatestVersion,
    string StoreName,
    string? StoreUrl = null,
    string? ReleaseNotes = null,
    string? ExpectedSha256 = null);

//интерфейс сервиса проверки обновлений программы
public interface IAppUpdaterService
{
    //проверка обновлений
    public Task CheckForUpdatesAsync(bool manualCheck = false);

    //получение информации о версии из стора
    public Task<StoreUpdateInfo> CheckVersionAsync();
}
