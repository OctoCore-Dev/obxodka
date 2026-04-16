namespace obxodka.Pages;
internal partial class UserProfilePage : ContentPage
{
    private readonly IVpnService _vpnService;
    private long _remainingSeconds;
    private bool _isPageActive;
    private int _tempSelectedTheme = -1;
    public UserProfilePage(IVpnService vpnService)
    {
        InitializeComponent();
        _vpnService = vpnService;
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _isPageActive = true;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateThemeButtons(Preferences.Default.Get("SelectedTheme", 0));
        });
        await PlayCascadeAnimationAsync();
        var session = await AuthManager.LoadSessionAsync().ConfigureAwait(true);
        EmailLabel.Text = !string.IsNullOrEmpty(session.Email) ? session.Email : "Гость";
        StartPulsingAnimation();
    }
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _isPageActive = false;
    }
    private async Task PlayCascadeAnimationAsync()
    {
        HeaderTitle.Opacity = 0; HeaderTitle.Scale = 0.8;
        BalanceCard.Opacity = 0; BalanceCard.Scale = 0.8;
        SettingsCard.Opacity = 0; SettingsCard.Scale = 0.8;
        AccountCard.Opacity = 0; AccountCard.Scale = 0.8;
        AdBlockCard.Opacity = 0; AdBlockCard.Scale = 0.8;
        SplitTunnelCard.Opacity = 0; SplitTunnelCard.Scale = 0.8;
        BottomSection.Opacity = 0; BottomSection.TranslationY = 30;
        _ = HeaderTitle.FadeToAsync(1, 400);
        _ = HeaderTitle.ScaleToAsync(1, 400, Easing.SpringOut);
        await Task.Delay(50);
        _ = BalanceCard.FadeToAsync(1, 500);
        _ = BalanceCard.ScaleToAsync(1, 500, Easing.SpringOut);
        await Task.Delay(50);
        _ = SettingsCard.FadeToAsync(1, 500);
        _ = SettingsCard.ScaleToAsync(1, 500, Easing.SpringOut);
        await Task.Delay(50);
        _ = AccountCard.FadeToAsync(1, 500);
        _ = AccountCard.ScaleToAsync(1, 500, Easing.SpringOut);
        await Task.Delay(50);
        _ = AdBlockCard.FadeToAsync(1, 500);
        _ = AdBlockCard.ScaleToAsync(1, 500, Easing.SpringOut);
        await Task.Delay(50);
        _ = SplitTunnelCard.FadeToAsync(1, 500);
        _ = SplitTunnelCard.ScaleToAsync(1, 500, Easing.SpringOut);
        await Task.Delay(50);
        _ = BottomSection.FadeToAsync(1, 400);
        await BottomSection.TranslateToAsync(0, 0, 400, Easing.CubicOut);
    }
    private void OnThemeSelected(object? sender, TappedEventArgs e)
    {
        if (int.TryParse(e.Parameter?.ToString(), out int themeId))
        {
            _tempSelectedTheme = themeId;
            UpdateThemeButtons(themeId);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                ApplyThemeBtn.IsEnabled = true;
            });
            try { Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(10)); } catch { }
        }
    }
    private async void OnApplyThemeClicked(object? sender, EventArgs e)
    {
        if (_tempSelectedTheme == -1) return;
        LoadingOverlay.IsVisible = true;
        await LoadingOverlay.FadeToAsync(1, 250).ConfigureAwait(true);
        await Task.Delay(600).ConfigureAwait(true);
        ThemeManager.SetTheme((ObxodkaTheme)_tempSelectedTheme);
        await Task.Delay(300).ConfigureAwait(true);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateThemeButtons(_tempSelectedTheme);
            ApplyThemeBtn.IsEnabled = false;
        });
        await LoadingOverlay.FadeToAsync(0, 250).ConfigureAwait(true);
        LoadingOverlay.IsVisible = false;
    }
    private void UpdateThemeButtons(int selectedId)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var activeColor = MauiColor.FromArgb("#00AAFF");
            var idleColor = MauiColor.FromArgb("#30FFFFFF");
            try
            {
                if (Application.Current?.Resources != null)
                {
                    if (Application.Current.Resources.TryGetValue("StatusBlue", out var statusVal) && statusVal is MauiColor sColor)
                        activeColor = sColor;
                    if (Application.Current.Resources.TryGetValue("BorderColor", out var borderVal) && borderVal is MauiColor bColor)
                        idleColor = bColor;
                }
            }
            catch (Exception) { }
            if (BtnDark != null) BtnDark.Stroke = selectedId == 0 ? activeColor : idleColor;
            if (BtnLight != null) BtnLight.Stroke = selectedId == 1 ? activeColor : idleColor;
            if (BtnGlass != null) BtnGlass.Stroke = selectedId == 2 ? activeColor : idleColor;
        });
    }
    private async void OnAdBlockToggled(object? sender, ToggledEventArgs e)
    {
        if (!_isPageActive) return;
        Preferences.Default.Set("use_adblock_dns", e.Value);
        var mainPage = IPlatformApplication.Current?.Services.GetService<MainPage>();
        if (mainPage?.IsVpnRunning == true) await mainPage.RestartVpnAsync().ConfigureAwait(true);
    }
    private void SyncTimeFromMainPage()
    {
        var mainPage = IPlatformApplication.Current?.Services?.GetService<MainPage>();
        if (mainPage != null)
        {
            _remainingSeconds = mainPage.RemainingSeconds;
            MainThread.BeginInvokeOnMainThread(UpdateBalanceUI);
        }
    }
    private void UpdateBalanceUI()
    {
        long tokens = _remainingSeconds / 3600;
        long restSeconds = _remainingSeconds % 3600;
        var t = TimeSpan.FromSeconds(restSeconds);
        TokenAmountLabel.Text = $"{tokens}T / {t.Minutes:D2}:{t.Seconds:D2}";
    }
    private async void StartPulsingAnimation()
    {
        while (_isPageActive)
        {
            SyncTimeFromMainPage();
            await TokenAmountLabel.ScaleToAsync(1.05, 1000, Easing.SinInOut).ConfigureAwait(true);
            if (!_isPageActive) break;
            await TokenAmountLabel.ScaleToAsync(1.0, 1000, Easing.SinInOut).ConfigureAwait(true);
        }
    }
    private async void OnLogoutClicked(object? sender, EventArgs e)
    {
        _vpnService.StopVpn();
        await AuthManager.RemoveCurrentDeviceFromServerAsync().ConfigureAwait(true);
        AuthManager.ClearSession();
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var loginPage = Handler?.MauiContext?.Services.GetRequiredService<LoginPage>();
            if (Application.Current?.Windows.Count > 0 && loginPage != null)
            {
                Application.Current.Windows[0].Page = new NavigationPage(loginPage);
            }
        });
    }
    private async void OnBuyTokensClicked(object? sender, EventArgs e) => await PushPageAsync<PaymentPage>().ConfigureAwait(true);
    private async void OnChangePasswordClicked(object? sender, EventArgs e) => await PushPageAsync<ChangePasswordPage>().ConfigureAwait(true);
    private async void OnDevicesClicked(object? sender, EventArgs e) => await PushPageAsync<DevicesPage>().ConfigureAwait(true);
    private async void OnDeleteAccountClicked(object? sender, EventArgs e) => await PushPageAsync<DeleteAccountPage>().ConfigureAwait(true);
    private async void OnSplitTunnelingClicked(object? sender, EventArgs e)
    {
        try
        {
            var page = new SplitTunnelingPage(_vpnService);
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (Navigation != null)
                {
                    await Navigation.PushAsync(page).ConfigureAwait(false);
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NAV ERROR] SplitTunneling: {ex.Message}");
        }
    }
    private async Task PushPageAsync<T>() where T : Page
    {
        var page = Handler?.MauiContext?.Services.GetRequiredService<T>();
        if (page != null) await Navigation.PushAsync(page).ConfigureAwait(true);
    }
}