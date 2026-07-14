using Android.Content;
using Application = Android.App.Application;

namespace obxodka.Services;

public class AndroidAppUpdaterService : IAppUpdaterService, IDisposable
{
    private readonly HttpClient _httpClient;

    public AndroidAppUpdaterService() => _httpClient = new HttpClient();

    public void Dispose()
    {
        _httpClient?.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task CheckForUpdatesAsync(bool manualCheck = false)
    {
        try
        {
            var updateUrl = $"{AppConfig.ApiBaseUrl}updates.json?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            var response = await _httpClient.GetStringAsync(updateUrl);
            var root = JsonSerializer.Deserialize(response, AppJsonContext.Default.UpdatesRoot);
            var updateInfo = root?.Android;

            if (updateInfo == null)
            {
                return;
            }

            if (int.TryParse(AppInfo.Current.BuildString, out var currentBuild))
            {
                if (updateInfo.VersionCode > currentBuild)
                {
                    var wantUpdate = false;
                    if (Shell.Current != null)
                    {
                        wantUpdate = await MainThread.InvokeOnMainThreadAsync(() =>
                            Shell.Current.DisplayAlertAsync(
                                "Доступно обновление!",
                                $"Вышла новая версия {updateInfo.Version}.\n\nЧто нового:\n{updateInfo.ReleaseNotes}\n\nОбновить сейчас?",
                                "Да", "Позже"));
                    }

                    if (wantUpdate)
                    {
                        await DownloadAndInstallAsync(updateInfo.Url);
                    }
                }
                else if (manualCheck)
                {
                    if (Shell.Current != null)
                    {
                        await MainThread.InvokeOnMainThreadAsync(() =>
                            Shell.Current.DisplayAlertAsync("Обновлений нет", "У вас установлена последняя версия приложения.", "OK"));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update check failed: {ex}");
            if (manualCheck && Shell.Current != null)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                    Shell.Current.DisplayAlertAsync("Ошибка", "Не удалось проверить обновления.", "OK"));
            }
        }
    }

    private async Task DownloadAndInstallAsync(string downloadUrl)
    {
        try
        {
            var apkName = "update.apk";
            var cacheDir = FileSystem.Current.CacheDirectory;
            var apkPath = Path.Combine(cacheDir, apkName);

            if (Shell.Current != null)
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    var toast = CommunityToolkit.Maui.Alerts.Toast.Make("Скачивание обновления...", CommunityToolkit.Maui.Core.ToastDuration.Long);
                    await toast.Show();
                });
            }

            using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            _ = response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(apkPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await stream.CopyToAsync(fileStream);
            fileStream.Close();
            InstallApk(apkPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update download failed: {ex}");
            if (Shell.Current != null)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                    Shell.Current.DisplayAlertAsync("Ошибка", "Не удалось скачать обновление.", "OK"));
            }
        }
    }

    private static void InstallApk(string apkPath)
    {
        try
        {
            var context = Application.Context;
            var file = new Java.IO.File(apkPath);
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(context, context.PackageName + ".fileprovider", file);

            var intent = new Intent(Intent.ActionView);
            _ = intent.SetDataAndType(uri, "application/vnd.android.package-archive");
            _ = intent.SetFlags(ActivityFlags.ClearTop | ActivityFlags.NewTask);
            _ = intent.AddFlags(ActivityFlags.GrantReadUriPermission);

            context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to start install intent: {ex}");
        }
    }
}

public class UpdatesRoot
{
    public UpdateInfo? Android { get; set; }
}

public class UpdateInfo
{
    public string Version { get; set; } = string.Empty;
    public int VersionCode { get; set; }
    public string Url { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
}
