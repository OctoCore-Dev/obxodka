using Android.Content;
using Application = Android.App.Application;
using Uri = Android.Net.Uri;

namespace obxodka.Services;

//сервис проверки обновлений приложения в Google Play для android
public sealed class AndroidAppUpdaterService : IAppUpdaterService
{
    private static readonly HttpClient s_httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    //проверка наличия новой версии приложения в Google Play
    public async Task<StoreUpdateInfo> CheckVersionAsync()
    {
        var currentVersion = AppInfo.Current.VersionString;
        var packageName = Application.Context.PackageName ?? "com.octocore.obxodka";
        var storeUrl = $"https://play.google.com/store/apps/details?id={packageName}&hl=ru";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, storeUrl);
            req.Headers.Add("User-Agent", "Mozilla/5.0 (Linux; Android 14; Mobile)");

            using var resp = await s_httpClient.SendAsync(req).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var html = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var (storeVersion, whatsNew) = StoreVersionParser.ParseGooglePlayHtml(html);

                if (!string.IsNullOrWhiteSpace(storeVersion))
                {
                    var hasUpdate = StoreVersionParser.IsNewerVersion(currentVersion, storeVersion);
                    return new StoreUpdateInfo(
                        hasUpdate,
                        currentVersion,
                        storeVersion,
                        "Google Play",
                        storeUrl,
                        whatsNew);
                }
            }
        }
        catch (HttpRequestException ex)
        {
            AppLogger.LogWarning($"[PLAY STORE VERSION CHECK] Network unavailable or connection failed: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            //тайм-аут или отмена проверки обновлений
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[PLAY STORE VERSION CHECK] Check failed: {ex.Message}");
        }

        return new StoreUpdateInfo(
            false,
            currentVersion,
            currentVersion,
            "Google Play",
            storeUrl);
    }

    //запуск проверки обновлений и показ диалога пользователю
    public async Task CheckForUpdatesAsync(bool manualCheck = false)
    {
        try
        {
            var updateInfo = await CheckVersionAsync().ConfigureAwait(false);
            if (updateInfo.HasUpdate)
            {
                MainThread.BeginInvokeOnMainThread(async void () =>
                {
                    try
                    {
                        var notes = string.IsNullOrWhiteSpace(updateInfo.ReleaseNotes)
                            ? string.Empty
                            : $"\n\nЧто нового:\n{updateInfo.ReleaseNotes.Trim()}";
                        if (notes.Length > 450)
                        {
                            notes = notes[..450] + "...";
                        }

                        var msg =
                            $"В Google Play доступна новая версия v{updateInfo.LatestVersion} (у вас установлена v{updateInfo.CurrentVersion}).{notes}\n\nЖелаете обновить приложение сейчас?";
                        var update = await NeoAlert.ShowConfirmAsync("Доступно обновление", msg, "Обновить", "Позже");

                        if (update)
                        {
                            await OpenPlayStoreAsync();
                        }
                    }
                    catch
                    {
                        // ignored
                    }
                });
            }
            else if (manualCheck)
            {
                MainThread.BeginInvokeOnMainThread(async void () =>
                {
                    try
                    {
                        var msg = $"У вас установлена самая свежая версия (v{updateInfo.CurrentVersion}).";
                        await NeoAlert.ShowAsync("Обновления", msg);
                    }
                    catch
                    {
                        // ignored
                    }
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[ANDROID UPDATE CHECK] {ex.Message}");
            if (manualCheck)
            {
                await OpenPlayStoreAsync();
            }
        }
    }

    //открытие страницы приложения в магазине Google Play
    public static async Task OpenPlayStoreAsync()
    {
        var packageName = Application.Context.PackageName ?? "com.octocore.obxodka";
        try
        {
            using var intent = new Intent(Intent.ActionView, Uri.Parse($"market://details?id={packageName}"));
            _ = intent.SetPackage("com.android.vending");
            _ = intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
            Application.Context.StartActivity(intent);
        }
        catch
        {
            try
            {
                using var fallbackIntent = new Intent(Intent.ActionView, Uri.Parse($"market://details?id={packageName}"));
                _ = fallbackIntent.AddFlags(ActivityFlags.NewTask);
                Application.Context.StartActivity(fallbackIntent);
            }
            catch
            {
                try
                {
                    _ = await Launcher.OpenAsync(new System.Uri($"https://play.google.com/store/apps/details?id={packageName}"));
                }
                catch (Exception ex)
                {
                    AppLogger.LogError($"[PLAY STORE ERROR] {ex.Message}", ex);
                }
            }
        }
    }
}
