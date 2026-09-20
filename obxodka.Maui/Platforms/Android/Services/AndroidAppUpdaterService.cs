using Android.Content;
using Application = Android.App.Application;

namespace obxodka.Services;

public sealed class AndroidAppUpdaterService : IAppUpdaterService
{
    private static readonly HttpClient t_httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    public async Task<StoreUpdateInfo> CheckVersionAsync()
    {
        var currentVersion = AppInfo.Current.VersionString;
        var packageName = Application.Context?.PackageName ?? "com.octocore.obxodka";
        var storeUrl = $"https://play.google.com/store/apps/details?id={packageName}&hl=ru";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, storeUrl);
            req.Headers.Add("User-Agent", "Mozilla/5.0 (Linux; Android 14; Mobile)");

            using var resp = await t_httpClient.SendAsync(req).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var html = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var (storeVersion, whatsNew) = StoreVersionParser.ParseGooglePlayHtml(html);

                if (!string.IsNullOrWhiteSpace(storeVersion))
                {
                    var hasUpdate = StoreVersionParser.IsNewerVersion(currentVersion, storeVersion);
                    return new StoreUpdateInfo(
                        HasUpdate: hasUpdate,
                        CurrentVersion: currentVersion,
                        LatestVersion: storeVersion,
                        StoreName: "Google Play",
                        StoreUrl: storeUrl,
                        ReleaseNotes: whatsNew);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PLAY STORE VERSION CHECK ERROR] {ex.Message}");
        }

        return new StoreUpdateInfo(
            HasUpdate: false,
            CurrentVersion: currentVersion,
            LatestVersion: currentVersion,
            StoreName: "Google Play",
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
                    var msg = $"В Google Play доступна новая версия v{updateInfo.LatestVersion} (у вас установлена v{updateInfo.CurrentVersion}).\n\nЖелаете обновить приложение сейчас?";
                    var update = await NeoAlert.ShowConfirmAsync("Доступно обновление", msg, "Обновить", "Позже");

                    if (update)
                    {
                        await OpenPlayStoreAsync();
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
            Debug.WriteLine($"[ANDROID UPDATE ERROR] {ex.Message}");
            if (manualCheck)
            {
                await OpenPlayStoreAsync();
            }
        }
    }

    public static async Task OpenPlayStoreAsync()
    {
        var packageName = Application.Context?.PackageName ?? "com.octocore.obxodka";
        try
        {
            var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse($"market://details?id={packageName}"));
            _ = intent.SetPackage("com.android.vending");
            _ = intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
            Application.Context?.StartActivity(intent);
        }
        catch
        {
            try
            {
                var fallbackIntent = new Intent(Intent.ActionView, Android.Net.Uri.Parse($"market://details?id={packageName}"));
                _ = fallbackIntent.AddFlags(ActivityFlags.NewTask);
                Application.Context?.StartActivity(fallbackIntent);
            }
            catch
            {
                try
                {
                    _ = await Launcher.OpenAsync(new Uri($"https://play.google.com/store/apps/details?id={packageName}"));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PLAY STORE ERROR] {ex.Message}");
                }
            }
        }
    }
}
