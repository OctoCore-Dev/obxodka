namespace obxodka.Pages;
internal partial class MainPage : ContentPage, IDisposable
{
    private readonly IVpnService _vpnService;
    private readonly ApiService _apiService;
    private bool _isBusy;
    private long _remainingSeconds;
    private CancellationTokenSource? _vpnCts;
    private bool _hasFetchedInitialBalance;
    private bool _isVisualsAnimating = false;
    public long RemainingSeconds => _remainingSeconds;
    public bool IsVpnRunning => _vpnService.IsRunning;
    public MainPage(IVpnService vpnService, ApiService apiService)
    {
        InitializeComponent();
        _vpnService = vpnService;
        _apiService = apiService;
    }
    public void Dispose()
    {
        _vpnCts?.Cancel();
        _vpnCts?.Dispose();
        _vpnCts = null;
        _isVisualsAnimating = false;
        GC.SuppressFinalize(this);
    }
    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width > 0)
        {
            double dynamicFontSize = width * 0.18;
            LogoLabel.FontSize = Math.Min(dynamicFontSize, 95);
        }
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        RefreshThemeColors();
        await PlayPageAnimationAsync().ConfigureAwait(true);
        if (App.PendingTileAction)
        {
            App.PendingTileAction = false;
            Dispatcher.Dispatch(async () =>
            {
                await Task.Delay(300).ConfigureAwait(true);
                if (!_isBusy) OnConnectClicked(this, EventArgs.Empty);
            });
        }
        _vpnService.OnStateChanged -= HandleAppVpnStateChanged;
        _vpnService.OnErrorOccurred -= HandleVpnError;
        _vpnService.OnStateChanged += HandleAppVpnStateChanged;
        _vpnService.OnErrorOccurred += HandleVpnError;
        bool running = _vpnService.IsRunning;
        HandleAppVpnStateChanged(running ? AppVpnState.Connected : AppVpnState.Disconnected);
        if (!running && !_hasFetchedInitialBalance)
        {
            await SyncPingWithServerAsync().ConfigureAwait(true);
            _hasFetchedInitialBalance = true;
        }
    }
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vpnService.OnStateChanged -= HandleAppVpnStateChanged;
        _vpnService.OnErrorOccurred -= HandleVpnError;
        _isVisualsAnimating = false;
    }
    private MauiColor GetThemeColor(string key, MauiColor fallback)
    {
        if (Application.Current?.Resources != null &&
            Application.Current.Resources.TryGetValue(key, out var val) && val is MauiColor color)
        {
            return color;
        }
        return fallback;
    }
    private void RefreshThemeColors()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_vpnService.IsRunning)
            {
                MauiColor waveColor = GetThemeColor("StatusBlue", Colors.DeepSkyBlue);
                MauiColor baseBtnColor = GetThemeColor("SurfaceDark", Colors.DarkSlateGray);
                ConnectButtonBorder.BackgroundColor = baseBtnColor;
                SideWave.Fill = new SolidColorBrush(waveColor);
                if (ConnectButtonShadow != null) ConnectButtonShadow.Brush = new SolidColorBrush(waveColor);
            }
            else
            {
                MauiColor primaryAccent = GetThemeColor("PrimaryAccent", Colors.Crimson);
                ConnectButtonBorder.BackgroundColor = primaryAccent;
                if (ConnectButtonShadow != null) ConnectButtonShadow.Brush = new SolidColorBrush(primaryAccent);
            }
        });
    }
    private async Task PlayPageAnimationAsync()
    {
        TopCardsGrid.Opacity = 0; TopCardsGrid.TranslationY = -40;
        LogoLabel.Opacity = 0; LogoLabel.Scale = 0.8;
        ConnectButtonBorder.Opacity = 0; ConnectButtonBorder.Scale = 0.5;
        BottomStack.Opacity = 0; BottomStack.TranslationY = 40;
        _ = TopCardsGrid.FadeToAsync(1, 600, Easing.CubicOut);
        _ = TopCardsGrid.TranslateToAsync(0, 0, 600, Easing.CubicOut);
        await Task.Delay(150).ConfigureAwait(true);
        _ = LogoLabel.FadeToAsync(1, 600, Easing.CubicOut);
        _ = LogoLabel.ScaleToAsync(1, 600, Easing.SpringOut);
        await Task.Delay(150).ConfigureAwait(true);
        _ = ConnectButtonBorder.FadeToAsync(1, 600, Easing.CubicOut);
        _ = ConnectButtonBorder.ScaleToAsync(1, 600, Easing.SpringOut);
        await Task.Delay(150).ConfigureAwait(true);
        _ = BottomStack.FadeToAsync(1, 600, Easing.CubicOut);
        await BottomStack.TranslateToAsync(0, 0, 600, Easing.CubicOut).ConfigureAwait(true);
    }
    private void StartVisualEffects()
    {
        if (_isVisualsAnimating) return;
        _isVisualsAnimating = true;
        MauiColor waveColor = GetThemeColor("StatusBlue", Colors.DeepSkyBlue);
        MauiColor baseBg = GetThemeColor("SurfaceDark", Colors.DarkSlateGray);
        SideWave.Fill = new SolidColorBrush(waveColor);
        PulseRing.Stroke = new SolidColorBrush(waveColor);
        AnimateBackgroundColor(ConnectButtonBorder, ConnectButtonBorder.BackgroundColor ?? Colors.Transparent, baseBg, 600, Easing.CubicOut);
        _ = AmbientGlowContainer.FadeToAsync(1, 1500, Easing.CubicOut);
        _ = SideWave.TranslateToAsync(-560, 0, 800, Easing.CubicOut);
        var ambientAnim = new Microsoft.Maui.Controls.Animation();
        ambientAnim.Add(0, 0.5, new Microsoft.Maui.Controls.Animation(v => ConnectButtonBorder.Scale = v, 1, 1.03, Easing.SinInOut));
        ambientAnim.Add(0.5, 1, new Microsoft.Maui.Controls.Animation(v => ConnectButtonBorder.Scale = v, 1.03, 1, Easing.SinInOut));
        ambientAnim.Add(0, 1, new Microsoft.Maui.Controls.Animation(v => PulseRing.Scale = v, 0.9, 1.3, Easing.CubicOut));
        ambientAnim.Add(0, 0.8, new Microsoft.Maui.Controls.Animation(v => PulseRing.Opacity = v, 0.6, 0, Easing.CubicOut));
        ambientAnim.Add(0, 1, new Microsoft.Maui.Controls.Animation(v =>
        {
            double rad = v * Math.PI * 2;
            GlowBlob1.TranslationX = Math.Sin(rad) * 120 - 150;
            GlowBlob1.TranslationY = Math.Cos(rad) * 80 - 100;
            GlowBlob1.Scale = 1 + (Math.Sin(rad * 2) * 0.2);
            GlowBlob2.TranslationX = Math.Cos(rad) * 150 + 200;
            GlowBlob2.TranslationY = Math.Sin(rad) * 100 + 150;
            GlowBlob3.TranslationX = Math.Sin(rad) * 90;
            GlowBlob3.Scale = 1 + (Math.Cos(rad) * 0.3);
        }));
        ambientAnim.Commit(this, "ActiveVpnVisuals", length: 4000, repeat: () => _isVisualsAnimating);
    }
    private void StopVisualEffects()
    {
        if (!_isVisualsAnimating) return;
        _isVisualsAnimating = false;
        this.AbortAnimation("ActiveVpnVisuals");
        _ = ConnectButtonBorder.ScaleToAsync(1, 400, Easing.SpringOut);
        _ = PulseRing.FadeToAsync(0, 300);
        _ = AmbientGlowContainer.FadeToAsync(0, 800, Easing.CubicOut);
        _ = SideWave.TranslateToAsync(-1000, 0, 600, Easing.CubicIn);
        MauiColor primaryAccent = GetThemeColor("PrimaryAccent", Colors.Crimson);
        AnimateBackgroundColor(ConnectButtonBorder, ConnectButtonBorder.BackgroundColor ?? Colors.Transparent, primaryAccent, 600, Easing.CubicOut);
    }
    private void AnimateTextColor(Label target, MauiColor from, MauiColor to, uint duration, Easing ease)
    {
        string animName = $"TextColorAnim_{target.GetHashCode()}";
        this.AbortAnimation(animName); 
        var animation = new Microsoft.Maui.Controls.Animation(v => target.TextColor = from.Lerp(to, (float)v), 0, 1, ease);
        animation.Commit(this, animName, length: duration);
    }
    private void AnimateBackgroundColor(Border target, MauiColor from, MauiColor to, uint duration, Easing ease)
    {
        string animName = $"BgColorAnim_{target.GetHashCode()}";
        this.AbortAnimation(animName); 
        var animation = new Microsoft.Maui.Controls.Animation(v => target.BackgroundColor = from.Lerp(to, (float)v), 0, 1, ease);
        animation.Commit(this, animName, length: duration);
    }
    private void HandleAppVpnStateChanged(AppVpnState state)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            switch (state)
            {
                case AppVpnState.Disconnected:
                    UpdateUiState(false, "ОТКЛЮЧЕНО", "ПУСК");
                    _vpnCts?.Cancel();
                    _vpnCts = null;
                    break;
                case AppVpnState.Connecting:
                case AppVpnState.Reconnecting:
                    UpdateUiState(false, "ЖДИТЕ...", "ЖДИТЕ");
                    break;
                case AppVpnState.Connected:
                    UpdateUiState(true, "ЗАЩИЩЕНО", "СТОП");
                    await SyncPingWithServerAsync().ConfigureAwait(true);
                    if (_vpnCts == null && _remainingSeconds > 0)
                    {
                        _vpnCts = new CancellationTokenSource();
                        _ = ConsumeTimeLoopAsync(_vpnCts.Token);
                    }
                    break;
                case AppVpnState.Error:
                    UpdateUiState(false, "ОШИБКА", "ПУСК");
                    _vpnCts?.Cancel();
                    _vpnCts = null;
                    break;
            }
        });
    }
    private void UpdateUiState(bool connected, string statusText, string buttonText)
    {
        uint duration = 600;
        Easing ease = Easing.CubicInOut;
        StatusLabel.Text = statusText;
        ConnectButton.Text = buttonText;
        MauiColor statusBlue = GetThemeColor("StatusBlue", Colors.DeepSkyBlue);
        MauiColor errorColor = GetThemeColor("ErrorColor", Colors.Red);
        MauiColor primaryAccent = GetThemeColor("PrimaryAccent", Colors.Crimson);
        if (connected)
        {
            AnimateTextColor(StatusLabel, StatusLabel.TextColor, statusBlue, duration, ease);
            if (ConnectButtonShadow != null) ConnectButtonShadow.Brush = new SolidColorBrush(statusBlue);
            StartVisualEffects();
        }
        else
        {
            AnimateTextColor(StatusLabel, StatusLabel.TextColor, errorColor, duration, ease);
            if (ConnectButtonShadow != null) ConnectButtonShadow.Brush = new SolidColorBrush(primaryAccent);
            StopVisualEffects();
        }
    }
    private async void OnConnectClicked(object? sender, EventArgs? e)
    {
        if (_isBusy) return;
        _isBusy = true;
        try
        {
            _ = ConnectButtonBorder.ScaleToAsync(0.95, 100).ContinueWith(t => ConnectButtonBorder.ScaleToAsync(1.0, 100));
            if (_vpnService.CurrentState == AppVpnState.Disconnected || _vpnService.CurrentState == AppVpnState.Error)
            {
                var session = await AuthManager.LoadSessionAsync().ConfigureAwait(true);
                var (success, loginData, error) = await _apiService.LoginAsync(new AuthRequest
                {
                    Email = session.Email ?? string.Empty,
                    Password = session.Password ?? string.Empty,
                    Hwid = DeviceHelper.GetHwid(),
                    DeviceName = DeviceInfo.Name
                }).ConfigureAwait(true);
                if (!success || loginData == null)
                {
                    await DisplayAlertAsync("Ошибка", error ?? "Ошибка входа", "OK").ConfigureAwait(true);
                    return;
                }
                await SyncPingWithServerAsync().ConfigureAwait(true);
                if (_remainingSeconds <= 0) return;
                bool useAdblock = Preferences.Default.Get("use_adblock_dns", false);
                await _vpnService.StartVpn(loginData.VpnLink ?? string.Empty, useAdblock).ConfigureAwait(true);
            }
            else
            {
                _vpnService.StopVpn();
                await _apiService.StopVpnOnServerAsync().ConfigureAwait(true);
            }
        }
        finally { _isBusy = false; }
    }
    private static async Task<long> MeasurePingAsync()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var pingUri = new Uri("https://www.google.com/generate_204");
            using var pingClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = await pingClient.GetAsync(pingUri).ConfigureAwait(false);
            sw.Stop();
            return response.IsSuccessStatusCode ? sw.ElapsedMilliseconds : 0;
        }
        catch { return 0; }
    }
    private void HandleVpnError(string errorMessage)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await DisplayAlertAsync("Сбой сети", errorMessage, "OK").ConfigureAwait(true);
        });
    }
    private async Task<bool> SyncPingWithServerAsync()
    {
        var (isActive, remaining) = await _apiService.SyncVpnStatusAsync().ConfigureAwait(true);
        _remainingSeconds = remaining;
        MainThread.BeginInvokeOnMainThread(UpdateBalanceUI);
        return isActive;
    }
    private async Task SendStopToServerAsync() => await _apiService.StopVpnOnServerAsync().ConfigureAwait(true);
    public async Task RestartVpnAsync()
    {
        if (!_vpnService.IsRunning || _isBusy) return;
        _isBusy = true;
        try
        {
            _vpnService.StopVpn();
            var session = await AuthManager.LoadSessionAsync().ConfigureAwait(true);
            var (success, loginData, error) = await _apiService.LoginAsync(new AuthRequest
            {
                Email = session.Email ?? "",
                Password = session.Password ?? "",
                Hwid = DeviceHelper.GetHwid(),
                DeviceName = DeviceInfo.Name
            }).ConfigureAwait(true);
            if (success && loginData != null && !string.IsNullOrEmpty(loginData.VpnLink))
            {
                bool useAdblock = Preferences.Default.Get("use_adblock_dns", false);
                await _vpnService.StartVpn(loginData.VpnLink, useAdblock).ConfigureAwait(true);
            }
        }
        finally { _isBusy = false; }
    }
    private async Task StopVpnInternalAsync()
    {
        _vpnService.StopVpn();
        await SendStopToServerAsync().ConfigureAwait(true);
    }
    private async Task ConsumeTimeLoopAsync(CancellationToken ct)
    {
        int pingCounter = 0;
        int networkCheckCounter = 0;
        MainThread.BeginInvokeOnMainThread(() => PingLabel.IsVisible = true);
        while (!ct.IsCancellationRequested && _remainingSeconds > 0)
        {
            try
            {
                await Task.Delay(1000, ct).ConfigureAwait(true);
                _remainingSeconds--;
                pingCounter++;
                networkCheckCounter++;
                if (networkCheckCounter >= 3)
                {
                    networkCheckCounter = 0;
                    _ = Task.Run(async () =>
                    {
                        long currentPing = await MeasurePingAsync().ConfigureAwait(true);
                        MauiColor pingColor = currentPing > 0 && currentPing < 150
                            ? GetThemeColor("StatusBlue", Colors.DeepSkyBlue)
                            : GetThemeColor("ErrorColor", Colors.Red);
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            PingLabel.Text = currentPing > 0 ? $"Ping: {currentPing} ms" : "Ping: Ошибка";
                            PingLabel.TextColor = pingColor;
                        });
                    }, ct);
                }
                if (pingCounter >= 10)
                {
                    bool isActiveOnServer = await SyncPingWithServerAsync().ConfigureAwait(true);
                    pingCounter = 0;
                    if (!isActiveOnServer || _remainingSeconds <= 0)
                    {
                        MainThread.BeginInvokeOnMainThread(async () =>
                        {
                            await StopVpnInternalAsync().ConfigureAwait(true);
                            await DisplayAlertAsync("Внимание", "Подключение остановлено.", "OK").ConfigureAwait(true);
                        });
                        break;
                    }
                }
                MainThread.BeginInvokeOnMainThread(() => UpdateBalanceUI());
            }
            catch (TaskCanceledException) { break; }
        }
        MainThread.BeginInvokeOnMainThread(() =>
        {
            PingLabel.IsVisible = false;
            if (_remainingSeconds <= 0 && _vpnService.IsRunning)
                _ = StopVpnInternalAsync();
        });
    }
    private void UpdateBalanceUI()
    {
        long tokens = _remainingSeconds / 3600;
        long restSeconds = _remainingSeconds % 3600;
        var t = TimeSpan.FromSeconds(restSeconds);
        TokenAmountLabel.Text = $"{tokens}T / {t.Minutes:D2}:{t.Seconds:D2}";
    }
    public void ExecuteConnectClickFromTile()
    {
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(300), () =>
        {
            if (!_isBusy)
            {
                OnConnectClicked(this, EventArgs.Empty);
            }
        });
    }
    private async void OnAccountHeaderTapped(object? sender, TappedEventArgs? e)
    {
        await ProfileCard.ScaleToAsync(0.95, 50).ConfigureAwait(true);
        await ProfileCard.ScaleToAsync(1.0, 50).ConfigureAwait(true);
        var profilePage = IPlatformApplication.Current?.Services.GetService<UserProfilePage>();
        if (profilePage != null)
        {
            await Navigation.PushAsync(profilePage).ConfigureAwait(true);
        }
    }
}