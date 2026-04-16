namespace obxodka.Pages;
internal sealed partial class UpdatePage : ContentPage
{
    private readonly UpdateInfo _info;
    public UpdatePage(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;
        VersionLabel.Text = $"Версия {_info.Version}";
        ReleaseNotesLabel.Text = _info.ReleaseNotes;
        if (_info.IsCritical)
        {
            SkipBtn.IsVisible = false;
        }
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _ = AppLogo.FadeToAsync(1, 400);
        _ = AppLogo.ScaleToAsync(1, 400, Easing.SpringOut);
        await Task.Delay(100);
        _ = VersionLabel.FadeToAsync(1, 400);
        _ = VersionLabel.ScaleToAsync(1, 400, Easing.SpringOut);
        await Task.Delay(100);
        _ = InfoBorder.FadeToAsync(1, 500);
        _ = InfoBorder.ScaleToAsync(1, 500, Easing.SpringOut);
        await Task.Delay(100);
        _ = ButtonsLayout.FadeToAsync(1, 500);
        _ = ButtonsLayout.ScaleToAsync(1, 500, Easing.SpringOut);
    }
    private async void OnUpdateClicked(object? sender, EventArgs e)
    {
        await UpdateBtn.BounceClickAsync();
        UpdateBtn.IsEnabled = false;
        await Task.WhenAll(
            ButtonsLayout.FadeToAsync(0, 300, Easing.CubicIn),
            ButtonsLayout.TranslateToAsync(0, 20, 300, Easing.CubicIn)
        );
        ButtonsLayout.IsVisible = false;
        ProgressLayout.Opacity = 0;
        ProgressLayout.IsVisible = true;
        await ProgressLayout.FadeToAsync(1, 400, Easing.CubicOut);
#if WINDOWS
        await DownloadAndInstallWindowsUpdateAsync(_info.WindowsUrl);
#elif ANDROID
        await DownloadAndInstallAndroidUpdateAsync(_info.AndroidUrl);
#endif
    }
#if ANDROID
    private async Task DownloadAndInstallAndroidUpdateAsync(string downloadUrl)
    {
        try
        {
            ProgressLabel.Text = "ПОДГОТОВКА К ЗАГРУЗКЕ...";
            await UpdateProgressBar.ProgressTo(0.1, 500, Easing.Linear);
            var cacheDir = Android.App.Application.Context.GetExternalCacheDirs()?.FirstOrDefault();
            if (cacheDir == null) throw new Exception("Не найден кэш");
            string filePath = Path.Combine(cacheDir.AbsolutePath, "obxodka_update.apk");
            if (File.Exists(filePath)) File.Delete(filePath);
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            long? totalBytes = response.Content.Headers.ContentLength;
            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            ProgressLabel.Text = "СКАЧИВАНИЕ ОБНОВЛЕНИЯ...";
            byte[] buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead);
                totalRead += bytesRead;
                if (totalBytes.HasValue)
                {
                    double progress = (double)totalRead / totalBytes.Value;
                    if (totalRead % (8192 * 10) == 0)
                    {
                        MainThread.BeginInvokeOnMainThread(() => UpdateProgressBar.Progress = progress);
                    }
                }
            }
            MainThread.BeginInvokeOnMainThread(() => UpdateProgressBar.Progress = 1.0);
            ProgressLabel.Text = "ОТКРЫВАЮ УСТАНОВЩИК...";
            await Task.Delay(500);
            var context = Android.App.Application.Context;
            var apkFile = new Java.IO.File(filePath);
            var authority = $"{context.PackageName}.fileprovider";
            var apkUri = AndroidX.Core.Content.FileProvider.GetUriForFile(context, authority, apkFile);
            var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(apkUri, "application/vnd.android.package-archive");
            intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
            context.StartActivity(intent);
            Application.Current?.Quit();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ANDROID UPDATE ERROR] {ex.Message}");
            ProgressLabel.Text = "ОШИБКА ЗАГРУЗКИ";
            await Task.Delay(1500);
            await Browser.Default.OpenAsync(downloadUrl, BrowserLaunchMode.SystemPreferred);
            await NavigateDirectlyAsync();
        }
    }
#endif
    private async void OnSkipClicked(object? sender, EventArgs e)
    {
        if (SkipBtn.IsEnabled == false) return;
        SkipBtn.IsEnabled = false;
        await SkipBtn.BounceClickAsync();
        await NavigateDirectlyAsync();
    }
    private async Task NavigateDirectlyAsync()
    {
        try
        {
            var session = await AuthManager.LoadSessionAsync();
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                var services = Application.Current?.Handler?.MauiContext?.Services;
                if (services == null || Application.Current?.Windows.Count == 0) return;
                Page nextPointer;
                if (string.IsNullOrEmpty(session.JwtToken))
                {
                    nextPointer = services.GetService<LoginPage>();
                }
                else
                {
                    nextPointer = services.GetService<MainPage>();
                }
                if (nextPointer != null)
                {
                    Application.Current.Windows[0].Page = new NavigationPage(nextPointer);
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NAV ERROR] {ex.Message}");
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                var services = Application.Current?.Handler?.MauiContext?.Services;
                var login = services?.GetService<LoginPage>();
                if (login != null && Application.Current?.Windows.Count > 0)
                {
                    Application.Current.Windows[0].Page = new NavigationPage(login);
                }
            });
        }
    }
#if WINDOWS
    private async Task DownloadAndInstallWindowsUpdateAsync(string downloadUrl)
    {
        try
        {
            ProgressLabel.Text = "СОЕДИНЕНИЕ...";
            await UpdateProgressBar.ProgressTo(0.1, 500, Easing.Linear);
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            ProgressLabel.Text = "ЗАГРУЗКА...";
            await UpdateProgressBar.ProgressTo(0.6, 1000, Easing.SinIn);
            string tempDir = Path.Combine(Path.GetTempPath(), "ObxodkaUpdates");
            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
            string fileName = downloadUrl.EndsWith(".msix") ? "update.msix" : "update.exe";
            string tempPath = Path.Combine(tempDir, fileName);
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(fs);
            }
            await UpdateProgressBar.ProgressTo(1.0, 300, Easing.SinOut);
            ProgressLabel.Text = "ЗАПУСК...";
            await Task.Delay(800);
            Process.Start(new ProcessStartInfo { FileName = tempPath, UseShellExecute = true, Verb = "runas" });
            Application.Current?.Quit();
            Environment.Exit(0);
        }
        catch
        {
            _ = ProgressLayout.ShakeErrorAsync();
            ProgressLabel.Text = "ОШИБКА СКАЧИВАНИЯ";
            await Task.Delay(1000);
            await NavigateDirectlyAsync();
        }
    }
#endif
}