using Android.Content;
using Application = Android.App.Application;

namespace obxodka.Services;

public sealed class AndroidAppUpdaterService : IAppUpdaterService
{
    public async Task CheckForUpdatesAsync(bool manualCheck = false)
    {
        if (manualCheck)
        {
            await OpenPlayStoreAsync();
            return;
        }

        try
        {
            var currentVersion = AppInfo.Current.VersionString;
            Debug.WriteLine($"[ANDROID UPDATE CHECK] Current App Version: {currentVersion}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ANDROID UPDATE CHECK ERROR] {ex.Message}");
        }
    }

    public static async Task OpenPlayStoreAsync()
    {
        var packageName = Application.Context.PackageName ?? "com.octocore.obxodka";
        try
        {
            var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse($"market://details?id={packageName}"));
            _ = intent.AddFlags(ActivityFlags.NewTask);
            Application.Context.StartActivity(intent);
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
