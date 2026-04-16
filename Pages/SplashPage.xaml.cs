namespace obxodka.Pages;
internal sealed partial class SplashPage : ContentPage
{
    private readonly AuthManager _authManager;
    public bool SkipUpdateCheck { get; set; } = false;
    public SplashPage(AuthManager authManager)
    {
        InitializeComponent();
        _authManager = authManager;
    }
    protected override void OnAppearing()
    {
        base.OnAppearing();
        AppLogo.Opacity = 0; AppLogo.Scale = 0.5;
        GlowEllipse.Opacity = 0; GlowEllipse.Scale = 0.5;
        _ = GlowEllipse.FadeToAsync(0.15, 1200, Easing.SinInOut);
        _ = GlowEllipse.ScaleToAsync(1, 1200, Easing.SpringOut);
        _ = AppLogo.FadeToAsync(1, 800);
        _ = AppLogo.ScaleToAsync(1, 800, Easing.SpringOut);
        _ = MainLoader.FadeToAsync(1, 500);
        _ = LoadingLabel.FadeToAsync(1, 500);
        _ = RunStartupSequenceAsync();
    }
    private async Task RunStartupSequenceAsync()
    {
        Debug.WriteLine("[STARTUP] Начинаем запуск...");
        MainThread.BeginInvokeOnMainThread(() =>
        {
            RetryButton.IsVisible = false;
            MainLoader.IsVisible = true;
        });
        UpdateLoadingText("ПРОВЕРКА СЕТИ...");
        bool hasInternet = await CheckInternetAsync();
        if (!hasInternet)
        {
#if WINDOWS
            CleanupNetworkAdapters();
            ResetSystemProxy();
#endif
            UpdateLoadingText("НЕТ ИНТЕРНЕТА", Colors.Red);
            MainThread.BeginInvokeOnMainThread(() => { RetryButton.IsVisible = true; });
            return;
        }
        UpdateLoadingText("АВТОРИЗАЦИЯ...");
        var session = await AuthManager.LoadSessionAsync();
        UpdateLoadingText("ПРОВЕРКА ОБНОВЛЕНИЙ...");
        bool isUpdating = await CheckUpdatesAsync(session.JwtToken);
        if (isUpdating)
        {
            Debug.WriteLine("[STARTUP] Найдено обновление, прерываем загрузку.");
            return;
        }
        Debug.WriteLine("[STARTUP] Обновлений нет или пропущено, идем дальше.");
        if (string.IsNullOrEmpty(session.JwtToken))
        {
            NavigateTo<LoginPage>();
        }
        else
        {
            NavigateTo<MainPage>();
        }
    }
#if WINDOWS
    private void CleanupNetworkAdapters()
    {
        try
        {
            var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            foreach (var adapter in interfaces)
            {
                bool isVirtual = adapter.Description.ToLower().Contains("tap") ||
                                 adapter.Description.ToLower().Contains("tun") ||
                                 adapter.Description.ToLower().Contains("wireguard") ||
                                 adapter.Description.ToLower().Contains("sing-box");
                if (isVirtual && adapter.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                {
                    var processInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "netsh",
                        Arguments = $"interface set interface name=\"{adapter.Name}\" admin=disabled",
                        WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                        CreateNoWindow = true,
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    System.Diagnostics.Process.Start(processInfo);
                }
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[CLEANUP ERROR] {ex.Message}"); }
    }
    private void ResetSystemProxy()
    {
        try
        {
            using var registry = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
            if (registry != null) { registry.SetValue("ProxyEnable", 0); registry.Flush(); }
        }
        catch { }
    }
#endif
    private async Task<bool> CheckInternetAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = await client.GetAsync("https://www.google.com/generate_204");
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }
    private async Task<bool> CheckUpdatesAsync(string? jwtToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            if (!string.IsNullOrEmpty(jwtToken))
            {
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwtToken);
            }
            var response = await client.GetAsync("https://obxodka.one/api/App/updateInfo");
            Debug.WriteLine($"[DEBUG] Статус сервера: {response.StatusCode}");
            if (!response.IsSuccessStatusCode) return false;
            var json = await response.Content.ReadAsStringAsync();
            var updateInfo = JsonSerializer.Deserialize<UpdateInfo>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            string localVer = AppInfo.Current.VersionString;
            string serverVer = updateInfo?.Version ?? "NULL";
            if (localVer.Trim() != serverVer.Trim() && serverVer != "NULL")
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (Application.Current?.Windows.Count > 0)
                    {
                        Application.Current.Windows[0].Page = new UpdatePage(updateInfo);
                    }
                });
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DEBUG] ОШИБКА ОБНОВЛЕНИЯ: {ex.Message}");
        }
        return false;
    }
    private void UpdateLoadingText(string text, Microsoft.Maui.Graphics.Color? color = null)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            LoadingLabel.Text = text;
            if (color != null) LoadingLabel.TextColor = color;
        });
    }
    private static void NavigateTo<T>() where T : Page
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var service = Application.Current?.Handler?.MauiContext?.Services.GetService<T>();
            if (service != null && Application.Current?.Windows.Count > 0)
            {
                Application.Current.Windows[0].Page = new NavigationPage(service);
            }
        });
    }
    private void OnRetryClicked(object? sender, EventArgs e) => _ = RunStartupSequenceAsync();
}