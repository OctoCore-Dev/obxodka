namespace obxodka.Maui.Theming.Services;

public sealed partial class ThemeManager
{
    public static IEnumerable<string> EnumerateAssetPaths(ThemeManifest manifest) =>
        ThemeStorageService.EnumerateAssetPaths(manifest);

    public static string ToMirrorUrl(string? rawUrl) =>
        ThemeCatalogClient.ToMirrorUrl(rawUrl);

    public static void NormalizeCatalogUrls(ThemeCatalogResponse catalog) =>
        ThemeCatalogClient.NormalizeCatalogUrls(catalog);

    public static Task<ThemeCatalogResponse?> FetchCatalogFromMirrorsAsync(CancellationToken ct) =>
        ThemeCatalogClient.FetchCatalogFromMirrorsAsync(ct);

    public Task<ThemeCatalogResponse> LoadCatalogAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        var cachePath = Path.Combine(FileSystem.AppDataDirectory, "theme_catalog_cache.json");
        return ThemeCatalogClient.LoadCatalogAsync(
            cachePath,
            forceRefresh,
            fresh => MainThread.BeginInvokeOnMainThread(() => OnCatalogUpdated?.Invoke(fresh)),
            ct);
    }

    public List<ThemeManifest> GetInstalledThemes() =>
        _storageService.GetInstalledThemes();

    public bool IsThemeInstalled(string themeId) =>
        _storageService.IsThemeInstalled(themeId);

    public static bool IsVersionNewer(string? currentVersion, string? remoteVersion) =>
        ThemeValidator.IsVersionNewer(currentVersion, remoteVersion);

    public string? GetInstalledThemeVersion(string themeId) =>
        _storageService.GetInstalledThemeVersion(themeId);

    public bool IsUpdateAvailable(string themeId, string? remoteVersion) =>
        _storageService.IsUpdateAvailable(themeId, remoteVersion);

    public bool DeleteTheme(string themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId))
        {
            return false;
        }

        if (ActiveTheme?.Id == themeId)
        {
            ResetToDefault();
        }

        return _storageService.DeleteTheme(themeId);
    }

    public Task<bool> DownloadAndInstallThemeAsync(string themeId, IProgress<double>? progress = null, CancellationToken ct = default) =>
        _storageService.DownloadAndInstallThemeAsync(
            themeId,
            progress,
            (id, p, isComp, success, err) => MainThread.BeginInvokeOnMainThread(() => ThemeDownloadProgressChanged?.Invoke(this, (id, p, isComp, success, err))),
            ct);

    public bool IsThemeDownloading(string themeId, out double progress) =>
        _storageService.IsThemeDownloading(themeId, out progress);

    public async Task<(bool Success, string? ErrorMessage, ThemeManifest? Manifest)> ImportThemeFromZipAsync(
        Stream zipStream,
        string fileName,
        CancellationToken ct = default)
    {
        var res = await _storageService.ImportThemeFromZipAsync(zipStream, fileName, ct).ConfigureAwait(false);
        if (res.Success && res.Manifest is not null)
        {
            _ = await ApplyThemeByIdAsync(res.Manifest.Id).ConfigureAwait(false);
        }

        return res;
    }
}
