#if WINDOWS
namespace obxodka.Services;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsAppUpdaterService : IAppUpdaterService
{
    public const string StoreProductId = "9NZXP5WR803J";
    public const string GitHubRepo = "OctoCore-Dev/obxodka";

    private static readonly HttpClient t_httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public static bool IsInstalledFromStore()
    {
        try
        {
            var package = Windows.ApplicationModel.Package.Current;
            return package.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.Store;
        }
        catch
        {
            return false;
        }
    }

    public static string GetCurrentAppVersion()
    {
        try
        {
            var v = AppInfo.Current.VersionString;
            if (!string.IsNullOrWhiteSpace(v) && v != "0.0.0.0" && v != "0.0.0" && v != "0.0")
            {
                return v;
            }
        }
        catch
        {
        }

        var asmVer = typeof(WindowsAppUpdaterService).Assembly.GetName().Version;
        return asmVer is not null && asmVer.Major > 0
            ? $"{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}"
            : "6.0.7";
    }

    public async Task<StoreUpdateInfo> CheckVersionAsync()
    {
        var currentVersion = GetCurrentAppVersion();

        if (IsInstalledFromStore())
        {
            var storeUrl = $"ms-windows-store://pdp/?productid={StoreProductId}";
            try
            {
                var catalogUrl = $"https://displaycatalog.mp.microsoft.com/v7/products/{StoreProductId}?market=RU&languages=ru-RU";
                using var req = new HttpRequestMessage(HttpMethod.Get, catalogUrl);
                req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

                using var resp = await t_httpClient.SendAsync(req).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var storeVersion = StoreVersionParser.ParseMicrosoftStoreCatalogJson(json);

                    if (!string.IsNullOrWhiteSpace(storeVersion))
                    {
                        var hasUpdate = StoreVersionParser.IsNewerVersion(currentVersion, storeVersion);
                        return new StoreUpdateInfo(
                            HasUpdate: hasUpdate,
                            CurrentVersion: currentVersion,
                            LatestVersion: storeVersion,
                            StoreName: "Microsoft Store",
                            StoreUrl: storeUrl);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WINDOWS STORE CATALOG CHECK ERROR] {ex.Message}");
            }

            return new StoreUpdateInfo(
                HasUpdate: false,
                CurrentVersion: currentVersion,
                LatestVersion: currentVersion,
                StoreName: "Microsoft Store",
                StoreUrl: storeUrl);
        }
        else
        {
            var fallbackUrl = $"https://github.com/{GitHubRepo}/releases/latest";
            try
            {
                var apiUrl = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";
                using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                req.Headers.Add("User-Agent", "Obxodka-Desktop-Updater");

                using var resp = await t_httpClient.SendAsync(req).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var (ghVersion, whatsNew, downloadUrl) = StoreVersionParser.ParseGitHubReleaseJson(json);

                    if (!string.IsNullOrWhiteSpace(ghVersion))
                    {
                        var hasUpdate = StoreVersionParser.IsNewerVersion(currentVersion, ghVersion);
                        return new StoreUpdateInfo(
                            HasUpdate: hasUpdate,
                            CurrentVersion: currentVersion,
                            LatestVersion: ghVersion,
                            StoreName: "GitHub Releases",
                            StoreUrl: downloadUrl ?? fallbackUrl,
                            ReleaseNotes: whatsNew);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GITHUB RELEASE CHECK ERROR] {ex.Message}");
            }

            return new StoreUpdateInfo(
                HasUpdate: false,
                CurrentVersion: currentVersion,
                LatestVersion: currentVersion,
                StoreName: "GitHub Releases",
                StoreUrl: fallbackUrl);
        }
    }

    public async Task CheckForUpdatesAsync(bool manualCheck = false)
    {
        try
        {
            if (IsInstalledFromStore() && !manualCheck)
            {
                return;
            }

            var updateInfo = await CheckVersionAsync().ConfigureAwait(false);
            if (updateInfo.HasUpdate)
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    if (IsInstalledFromStore())
                    {
                        var msg = $"В Microsoft Store доступна новая версия v{updateInfo.LatestVersion} (у вас установлена v{updateInfo.CurrentVersion}).\n\nЖелаете открыть страницу в Microsoft Store?";
                        var update = await NeoAlert.ShowConfirmAsync("Доступно обновление", msg, "Обновить", "Позже");
                        if (update)
                        {
                            await OpenStorePageAsync();
                        }
                    }
                    else
                    {
                        var notes = string.IsNullOrWhiteSpace(updateInfo.ReleaseNotes) ? string.Empty : $"\n\nЧто нового:\n{updateInfo.ReleaseNotes.Trim()}";
                        if (notes.Length > 280)
                        {
                            notes = notes[..280] + "...";
                        }
                        var msg = $"Доступна новая версия Obxodka v{updateInfo.LatestVersion} (у вас установлена v{updateInfo.CurrentVersion}).{notes}\n\nЖелаете скачать и установить обновление сейчас?";
                        var update = await NeoAlert.ShowConfirmAsync("Обновление Obxodka", msg, "Обновить", "Позже");
                        if (update)
                        {
                            await DownloadAndInstallGitHubUpdateAsync(updateInfo.StoreUrl, updateInfo.LatestVersion);
                        }
                    }
                });
            }
            else if (manualCheck)
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    var source = IsInstalledFromStore() ? "Microsoft Store" : "GitHub";
                    var msg = $"У вас установлена самая свежая версия v{updateInfo.CurrentVersion} ({source}).";
                    await NeoAlert.ShowAsync("Обновления", msg, "OK");
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WINDOWS UPDATE ERROR] {ex.Message}");
            if (manualCheck)
            {
                if (IsInstalledFromStore())
                {
                    await OpenStorePageAsync();
                }
                else
                {
                    _ = await Launcher.OpenAsync(new Uri($"https://github.com/{GitHubRepo}/releases/latest"));
                }
            }
        }
    }

    private static async Task DownloadAndInstallGitHubUpdateAsync(string? url, string version)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            _ = await Launcher.OpenAsync(new Uri($"https://github.com/{GitHubRepo}/releases/latest"));
            return;
        }

        if (url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var tempPath = Path.Combine(Path.GetTempPath(), $"Obxodka-Setup-v{version}.exe");
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("User-Agent", "Obxodka-Desktop-Updater");

                using var resp = await t_httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await resp.Content.CopyToAsync(fs).ConfigureAwait(false);
                    }

                    _ = Process.Start(new ProcessStartInfo(tempPath)
                    {
                        UseShellExecute = true
                    });

                    Environment.Exit(0);
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DOWNLOAD INSTALLER ERROR] {ex.Message}");
            }
        }

        _ = await Launcher.OpenAsync(new Uri(url));
    }

    public static async Task OpenStorePageAsync()
    {
        try
        {
            _ = await Launcher.OpenAsync(new Uri($"ms-windows-store://pdp/?productid={StoreProductId}"));
        }
        catch
        {
            try
            {
                _ = await Launcher.OpenAsync(new Uri("ms-windows-store://downloadsandupdates"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STORE LAUNCH ERROR] {ex.Message}");
            }
        }
    }
}
#endif
