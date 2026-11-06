namespace obxodka.Pages;

//боковая панель навигации для десктопной версии приложения
public sealed partial class DesktopSidebarView : ContentView
{
    private static readonly Color s_cyanColor = Color.FromArgb("#00E5FF");
    private static readonly Color s_mutedColor = Color.FromArgb("#6A5A8A");
    private static Color ActiveColor => (Application.Current?.Resources.TryGetValue("Primary", out var val) == true && val is Color c)
        ? c
        : Color.FromArgb("#7C3AED");
    private static Color ActiveBgColor => ActiveColor.WithAlpha(0.14f);
    private static readonly Color s_inactiveLightColor = Color.FromArgb("#9080B0");
    private static readonly Color s_inactiveDarkColor = Color.FromArgb("#6A5A8A");

    public event EventHandler<string>? NavTapped;
    public event EventHandler? LogoutTapped;
    public event EventHandler? SidebarWidthChanged;

    private IAppUpdaterService? _appUpdaterService;
    private bool _isExpanded;
    private bool _isVpnConnected;
    private string _currentTab = "vpn";

    //ширина занимаемого пространства для адаптивной верстки
    public double OccupiedWidth => IsVisible && DesktopSidebar.IsVisible
        ? (DesktopSidebar.WidthRequest > 0 ? DesktopSidebar.WidthRequest : 84) + 16
        : 0;

    public DesktopSidebarView()
    {
        InitializeComponent();
        AppVersionLabel.Text = $"v{AppInfo.Current.VersionString}";
        UpdateSeasonalLogo();
    }

    //передача сервиса обновлений
    public void Initialize(IAppUpdaterService? appUpdaterService = null) =>
        _appUpdaterService = appUpdaterService;

    //обновление сезонной версии логотипа
    public void UpdateSeasonalLogo() =>
        AppLogoImage.Source = ThemeManager.Instance?.ActiveTheme?.Id?.Contains("halloween", StringComparison.OrdinalIgnoreCase) == true ? "app_logo_halloween.png" : "app_logo.png";

    //анимация появления боковой панели
    public async Task PlayEntranceAnimationAsync()
    {
        DesktopSidebar.IsVisible = true;
        await UIAnimations.PlaySidebarEntranceAsync(
            DesktopSidebar,
            SideNavVpn,
            SideNavProfile,
            SideNavConfiguration,
            SideNavDevices,
            SideNavSplit,
            NavBug,
            NavReviews);
    }

    //скрытие боковой панели
    public void HideSidebar() => DesktopSidebar.IsVisible = false;

    //индикация подключения vpn и цвета статуса
    public void UpdateVpnStatus(bool isConnected)
    {
        _isVpnConnected = isConnected;
        var customStatus = ThemeManager.Instance?.ActiveTheme?.Vfx?.StatusText;
        if (isConnected)
        {
            var accent = (Application.Current?.Resources.TryGetValue("Accent", out var a) == true && a is Color ac) ? ac : s_cyanColor;
            VpnStatusDot.Color = accent;
            VpnStatusLabel.Text = !string.IsNullOrWhiteSpace(customStatus?.Connected) ? customStatus.Connected : "Защищено";
            VpnStatusLabel.TextColor = accent;
        }
        else
        {
            var muted = (Application.Current?.Resources.TryGetValue("TextMuted", out var m) == true && m is Color mc) ? mc : s_mutedColor;
            VpnStatusDot.Color = muted;
            VpnStatusLabel.Text = !string.IsNullOrWhiteSpace(customStatus?.Disconnected) ? customStatus.Disconnected : "Отключен";
            VpnStatusLabel.TextColor = muted;
        }
    }

    //актуализация цвета фона под активную тему
    public void UpdateCardOpacity()
    {
        var bgSurface = (Application.Current?.Resources.TryGetValue("BgSurface", out var bg) == true && bg is Color bgColor)
            ? bgColor
            : Color.FromArgb("#161622");
        var borderSubtle = (Application.Current?.Resources.TryGetValue("BorderSubtle", out var bs) == true && bs is Color bsColor)
            ? bsColor
            : Color.FromArgb("#282838");

        DesktopSidebar.BackgroundColor = bgSurface;
        DesktopSidebar.Stroke = borderSubtle;
    }

    //реакция на смену темы оформления
    public void OnThemeChanged() =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateCardOpacity();
            UpdateActiveTab(_currentTab);
            UpdateVpnStatus(_isVpnConnected);

            var textPrimary = (Application.Current?.Resources.TryGetValue("TextPrimary", out var tp) == true && tp is Color tpc)
                ? tpc
                : Colors.White;
            var textMuted = (Application.Current?.Resources.TryGetValue("TextMuted", out var tm) == true && tm is Color tmc)
                ? tmc
                : s_mutedColor;

            AppLogoText.TextColor = textPrimary;
            LabelVpn.TextColor = textPrimary;
            LabelProfile.TextColor = textPrimary;
            LabelConfiguration.TextColor = textPrimary;
            LabelDevices.TextColor = textPrimary;
            LabelSplit.TextColor = textPrimary;
            LabelBug.TextColor = textPrimary;
            LabelReviews.TextColor = textPrimary;

            NavBugIcon.IconColor = textMuted;
            NavReviewsIcon.IconColor = textMuted;
        });

    //разворачивание панели по клику в свернутом виде
    private void OnSidebarTapped(object? sender, TappedEventArgs e)
    {
        if (!_isExpanded)
        {
            ToggleSidebar();
        }
    }

    //сворачивание или разворачивание по нажатию на логотип
    private void OnLogoTapped(object? sender, TappedEventArgs e)
    {
        ThemeAudioService.PlayClick();
        _ = AppLogoImage.BounceClickAsync();
        ToggleSidebar();
    }

    //анимация аккордеона ширины панели и текста
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "MAUI Animation is managed and executed by the visual element animation extension")]
    private void ToggleSidebar()
    {
        _isExpanded = !_isExpanded;
        var targetWidth = _isExpanded ? 260 : 84;

        if (!_isExpanded)
        {
            _ = AppLogoImage.RotateToAsync(0, 200, Easing.CubicInOut);

            _ = AnimateLabelHideAsync(AppLogoText);
            _ = AnimateLabelHideAsync(LabelVpn);
            _ = AnimateLabelHideAsync(LabelProfile);
            _ = AnimateLabelHideAsync(LabelConfiguration);
            _ = AnimateLabelHideAsync(LabelDevices);
            _ = AnimateLabelHideAsync(LabelSplit);
            _ = AnimateLabelHideAsync(LabelBug);
            _ = AnimateLabelHideAsync(LabelReviews);
            _ = AnimateLabelHideAsync(VpnStatusLabel);
            _ = AnimateLabelHideAsync(AppVersionLabel);
            _ = AnimateLabelHideAsync(LogoutText);
            _ = LogoutIcon.FadeToAsync(1, 140);

            var anim = new Animation(v =>
            {
                DesktopSidebar.WidthRequest = v;
                SidebarWidthChanged?.Invoke(this, EventArgs.Empty);
            }, DesktopSidebar.Width, targetWidth);
            anim.Commit(this, "SidebarResize", 16, 200, Easing.CubicInOut, (_, _) => SidebarWidthChanged?.Invoke(this, EventArgs.Empty));
        }
        else
        {
            _ = AppLogoImage.RotateToAsync(360, 240, Easing.CubicOut);

            _ = AnimateLabelShowAsync(AppLogoText, 10);
            _ = AnimateLabelShowAsync(LabelVpn, 25);
            _ = AnimateLabelShowAsync(LabelProfile, 40);
            _ = AnimateLabelShowAsync(LabelConfiguration, 55);
            _ = AnimateLabelShowAsync(LabelDevices, 70);
            _ = AnimateLabelShowAsync(LabelSplit, 85);
            _ = AnimateLabelShowAsync(LabelBug, 100);
            _ = AnimateLabelShowAsync(LabelReviews, 115);
            _ = AnimateLabelShowAsync(VpnStatusLabel, 125);
            _ = AnimateLabelShowAsync(LogoutText, 135);
            _ = AnimateLabelShowAsync(AppVersionLabel, 145, 0.5);
            _ = LogoutIcon.FadeToAsync(0, 120);

            var anim = new Animation(v =>
            {
                DesktopSidebar.WidthRequest = v;
                SidebarWidthChanged?.Invoke(this, EventArgs.Empty);
            }, DesktopSidebar.Width, targetWidth);
            anim.Commit(this, "SidebarResize", 16, 220, Easing.CubicOut, (_, _) => SidebarWidthChanged?.Invoke(this, EventArgs.Empty));
        }
    }

    //плавный выезд подписи вкладки
    private static async Task AnimateLabelShowAsync(VisualElement? label, int delayMs, double targetOpacity = 1.0)
    {
        if (label is null)
        {
            return;
        }

        label.CancelAnimations();

        if (delayMs > 0)
        {
            await Task.Delay(delayMs);
        }

        label.TranslationX = -8;
        label.Opacity = 0;
        _ = label.TranslateToAsync(0, 0, 160, Easing.SpringOut);
        _ = label.FadeToAsync(targetOpacity, 140, Easing.CubicOut);
    }

    //скрытие подписи вкладки со смещением
    private static async Task AnimateLabelHideAsync(VisualElement? label)
    {
        if (label is null)
        {
            return;
        }

        label.CancelAnimations();
        _ = label.TranslateToAsync(-8, 0, 100, Easing.CubicIn);
        _ = await label.FadeToAsync(0, 100, Easing.CubicIn);
        label.TranslationX = 0;
    }

    //подсветка активной вкладки
    public void UpdateActiveTab(string tabName)
    {
        _currentTab = tabName;
        ResetAllSideNavItems();

        switch (tabName)
        {
            case "vpn":
                SideNavVpn.BackgroundColor = ActiveBgColor;
                NavVpnIcon.IconColor = ActiveColor;
                _ = SideNavVpn.ScaleToAsync(1.03, 150, Easing.SpringOut);
                _ = UIAnimations.PlayIconSpringHoverAsync(NavVpnIcon, 1.2);
                break;
            case "profile":
                SideNavProfile.BackgroundColor = ActiveBgColor;
                NavProfileIcon.IconColor = ActiveColor;
                _ = SideNavProfile.ScaleToAsync(1.03, 150, Easing.SpringOut);
                _ = UIAnimations.PlayIconBounceJumpAsync(NavProfileIcon, -3);
                break;
            case "configuration":
            case "themes":
                SideNavConfiguration.BackgroundColor = ActiveBgColor;
                NavConfigurationIcon.IconColor = ActiveColor;
                _ = SideNavConfiguration.ScaleToAsync(1.03, 150, Easing.SpringOut);
                _ = UIAnimations.PlayIconSpinAsync(NavConfigurationIcon, 90, 200);
                break;
            case "devices":
                SideNavDevices.BackgroundColor = ActiveBgColor;
                NavDevicesIcon.IconColor = ActiveColor;
                _ = SideNavDevices.ScaleToAsync(1.03, 150, Easing.SpringOut);
                _ = UIAnimations.PlayIconWiggleAsync(NavDevicesIcon, 12);
                break;
            case "split":
                SideNavSplit.BackgroundColor = ActiveBgColor;
                NavSplitIcon.IconColor = ActiveColor;
                _ = SideNavSplit.ScaleToAsync(1.03, 150, Easing.SpringOut);
                _ = UIAnimations.PlayIconSpringHoverAsync(NavSplitIcon, 1.2);
                break;
            default:
                break;
        }
    }

    //сброс визуального состояния всех пунктов меню
    private void ResetAllSideNavItems()
    {
        SideNavVpn.BackgroundColor = Colors.Transparent;
        SideNavProfile.BackgroundColor = Colors.Transparent;
        SideNavConfiguration.BackgroundColor = Colors.Transparent;
        SideNavDevices.BackgroundColor = Colors.Transparent;
        SideNavSplit.BackgroundColor = Colors.Transparent;

        SideNavVpn.Scale = 1.0;
        SideNavProfile.Scale = 1.0;
        SideNavConfiguration.Scale = 1.0;
        SideNavDevices.Scale = 1.0;
        SideNavSplit.Scale = 1.0;

        var inactiveColor = (Application.Current?.Resources.TryGetValue("TextMuted", out var tm) == true && tm is Color tmc)
            ? tmc
            : (Application.Current?.RequestedTheme == AppTheme.Light ? s_inactiveLightColor : s_inactiveDarkColor);

        NavVpnIcon.IconColor = inactiveColor;
        NavProfileIcon.IconColor = inactiveColor;
        NavConfigurationIcon.IconColor = inactiveColor;
        NavDevicesIcon.IconColor = inactiveColor;
        NavSplitIcon.IconColor = inactiveColor;
        NavBugIcon.IconColor = inactiveColor;
        NavReviewsIcon.IconColor = inactiveColor;
    }

    //выбор вкладки vpn
    private void OnNavVpnTapped(object? sender, TappedEventArgs e)
    {
        ThemeAudioService.PlayClick();
        _ = SideNavVpn.BounceClickAsync();
        NavTapped?.Invoke(this, "vpn");
    }

    //выбор вкладки профиля
    private void OnNavProfileTapped(object? sender, TappedEventArgs e)
    {
        ThemeAudioService.PlayClick();
        _ = SideNavProfile.BounceClickAsync();
        NavTapped?.Invoke(this, "profile");
    }

    //выбор вкладки конфигурации
    private void OnNavConfigurationTapped(object? sender, TappedEventArgs e)
    {
        ThemeAudioService.PlayClick();
        _ = SideNavConfiguration.BounceClickAsync();
        NavTapped?.Invoke(this, "configuration");
    }

    //выбор вкладки устройств
    private void OnNavDevicesTapped(object? sender, TappedEventArgs e)
    {
        ThemeAudioService.PlayClick();
        _ = SideNavDevices.BounceClickAsync();
        NavTapped?.Invoke(this, "devices");
    }

    //выбор вкладки разделения трафика
    private void OnNavSplitTapped(object? sender, TappedEventArgs e)
    {
        ThemeAudioService.PlayClick();
        _ = SideNavSplit.BounceClickAsync();
        NavTapped?.Invoke(this, "split");
    }

    //переход в баг-трекер в системном браузере
    private void OnNavBugTapped(object? sender, TappedEventArgs e)
    {
        ThemeAudioService.PlayClick();
        _ = NavBug.BounceClickAsync();
        try
        {
            _ = Browser.Default.OpenAsync(AppConfig.ApiUrl("BugTracker/Index"), BrowserLaunchMode.SystemPreferred);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[BUG TRACKER OPEN ERROR] {ex.Message}", ex);
        }
    }

    //переход к отзывам в системном браузере
    private void OnNavReviewsTapped(object? sender, TappedEventArgs e)
    {
        ThemeAudioService.PlayClick();
        _ = NavReviews.BounceClickAsync();
        try
        {
            _ = Browser.Default.OpenAsync(AppConfig.ApiUrl("Reviews/Index"), BrowserLaunchMode.SystemPreferred);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[REVIEWS OPEN ERROR] {ex.Message}", ex);
        }
    }

    //выход из учетной записи
    private void OnLogoutClicked(object? sender, TappedEventArgs e)
    {
        ThemeAudioService.PlayClick();
        _ = LogoutButtonBorder.BounceClickAsync();
        LogoutTapped?.Invoke(this, EventArgs.Empty);
    }

    //анимация наведения курсора мыши
    private async void OnPointerEnteredAsync(object? sender, PointerEventArgs e)
    {
        if (sender is not Border border)
        {
            return;
        }

        ThemeAudioService.PlayHover();
        _ = border.ScaleToAsync(1.04, 120, Easing.CubicOut);

        if (border == SideNavVpn)
        {
            await UIAnimations.PlayIconSpringHoverAsync(NavVpnIcon, 1.25);
        }
        else if (border == SideNavProfile)
        {
            await UIAnimations.PlayIconBounceJumpAsync(NavProfileIcon, -3);
        }
        else if (border == SideNavConfiguration)
        {
            await UIAnimations.PlayIconSpinAsync(NavConfigurationIcon, 180, 260);
        }
        else if (border == SideNavDevices)
        {
            await UIAnimations.PlayIconWiggleAsync(NavDevicesIcon, 14);
        }
        else if (border == SideNavSplit)
        {
            await UIAnimations.PlayIconSpringHoverAsync(NavSplitIcon, 1.25);
        }
        else if (border == NavBug)
        {
            await UIAnimations.PlayIconWiggleAsync(NavBugIcon, 16);
        }
        else if (border == NavReviews)
        {
            await UIAnimations.PlayIconTwinkleAsync(NavReviewsIcon);
        }
        else if (border == LogoutButtonBorder)
        {
            _ = LogoutIcon.ScaleToAsync(1.2, 120, Easing.SpringOut);
        }
    }

    //анимация ухода курсора мыши
    private async void OnPointerExitedAsync(object? sender, PointerEventArgs e)
    {
        if (sender is not Border border)
        {
            return;
        }

        _ = border.ScaleToAsync(1.0, 120, Easing.CubicIn);

        if (border == SideNavVpn)
        {
            await UIAnimations.PlayIconHoverExitAsync(NavVpnIcon);
        }
        else if (border == SideNavProfile)
        {
            await UIAnimations.PlayIconHoverExitAsync(NavProfileIcon);
        }
        else if (border == SideNavConfiguration)
        {
            await UIAnimations.PlayIconHoverExitAsync(NavConfigurationIcon);
        }
        else if (border == SideNavDevices)
        {
            await UIAnimations.PlayIconHoverExitAsync(NavDevicesIcon);
        }
        else if (border == SideNavSplit)
        {
            await UIAnimations.PlayIconHoverExitAsync(NavSplitIcon);
        }
        else if (border == NavBug)
        {
            await UIAnimations.PlayIconHoverExitAsync(NavBugIcon);
        }
        else if (border == NavReviews)
        {
            await UIAnimations.PlayIconHoverExitAsync(NavReviewsIcon);
        }
        else if (border == LogoutButtonBorder)
        {
            _ = LogoutIcon.ScaleToAsync(1.0, 120, Easing.CubicOut);
        }
    }

    //ручная проверка обновлений при клике на номер версии
    private async void OnVersionBadgeTappedAsync(object? sender, EventArgs e)
    {
        ThemeAudioService.PlayClick();
        if (sender is VisualElement ve)
        {
            _ = ve.BounceClickAsync();
        }

        var updater = _appUpdaterService ?? IPlatformApplication.Current?.Services?.GetService<IAppUpdaterService>();
        if (updater is not null)
        {
            await updater.CheckForUpdatesAsync(manualCheck: true);
        }
    }
}
