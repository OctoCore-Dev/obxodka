#if WINDOWS
namespace obxodka.Services;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsAppUpdaterService : IAppUpdaterService
{
    public const string StoreProductId = "9NZXP5WR803J";
    private static readonly HttpClient t_httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

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
            : "4.9.0";
    }

    public async Task<StoreUpdateInfo> CheckVersionAsync()
    {
        var currentVersion = GetCurrentAppVersion();
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

    public async Task CheckForUpdatesAsync(bool manualCheck = false)
    {
        try
        {
            var updateInfo = await CheckVersionAsync().ConfigureAwait(false);
            if (updateInfo.HasUpdate)
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    var msg = $"В Microsoft Store доступна новая версия v{updateInfo.LatestVersion} (у вас установлена v{updateInfo.CurrentVersion}).\n\nЖелаете обновить приложение сейчас?";
                    var update = await NeoAlert.ShowConfirmAsync("Доступно обновление", msg, "Обновить", "Позже");

                    if (update)
                    {
                        await OpenStorePageAsync();
                    }
                });
            }
            else if (manualCheck)
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    var msg = $"У вас установлена самая свежая версия (v{updateInfo.CurrentVersion}).";
                    await NeoAlert.ShowAsync("Обновления", msg, "OK");
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WINDOWS UPDATE ERROR] {ex.Message}");
            if (manualCheck)
            {
                await OpenStorePageAsync();
            }
        }
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
