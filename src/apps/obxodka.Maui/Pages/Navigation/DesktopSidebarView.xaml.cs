namespace obxodka.Pages.Navigation;

//боковая панель навигации (windows fluent 2 navigation view и android tablet navigation rail/drawer)
public sealed partial class DesktopSidebarView
{
    private const double CollapsedWidth = 76.0;
    private const double ExpandedWidth = 248.0;
    private const double SideMargin = 12.0;

    private readonly VisualElement[] _sidebarLabels;
    private IAppUpdaterService? _appUpdaterService;
    private string _currentTab = "vpn";
    private bool _isCompactHeight;
    private bool _isOverlayMode;
    private bool _isVpnConnected;
    private double _lastSafeBottom;
    private double _lastSafeTop;
    private int _toggleGeneration;

    public DesktopSidebarView()
    {
        InitializeComponent();
        _sidebarLabels =
        [
            AppLogoText,
            CollapseChevronIcon,
            LabelVpn,
            LabelProfile,
            LabelConfiguration,
            LabelDevices,
            LabelSplit,
            LabelBug,
            LabelReviews,
            VpnStatusLabel,
            LogoutText
        ];
        AppVersionLabel.Text = $"v{AppInfo.Current.VersionString}";
        UpdateSeasonalLogo();
    }

    private static Color GetResourceColor(string key, Color fallback) =>
        Application.Current?.Resources.TryGetValue(key, out var val) == true && val is Color c ? c : fallback;

    private static string GetResourceFont(string key, string fallback) =>
        Application.Current?.Resources.TryGetValue(key, out var val) == true && val is string s && !string.IsNullOrWhiteSpace(s)
            ? s
            : fallback;

    private static Color ActiveColor => GetResourceColor("Primary", Color.FromArgb("#0078D4"));

    private static Color ActiveBgColor => ActiveColor.WithAlpha(0.14f);

    private static Color HoverBgColor => GetResourceColor("BgElevated", Color.FromArgb("#202030")).WithAlpha(0.75f);

    private static Color InactiveTextColor => GetResourceColor("TextMuted", Color.FromArgb("#90FFFFFF"));

    public bool IsExpanded { get; private set; }

    //ширина занимаемого пространства для адаптивной верстки
    public double OccupiedWidth => IsVisible && DesktopSidebar.IsVisible
        ? (DesktopSidebar.WidthRequest > 0 ? DesktopSidebar.WidthRequest : CollapsedWidth) + SideMargin
        : 0;

    //режим наложения поверх контента для планшетов и узких окон
    public void SetOverlayMode(bool isOverlay)
    {
        _isOverlayMode = isOverlay;
        UpdateCardOpacity();
    }

    //сворачивание панели при открытой навигации в overlay-режиме
    public void Collapse()
    {
        if (IsExpanded)
        {
            ToggleSidebar();
        }
    }

    //учет безопасных отступов сверху и снизу на мобильных и планшетных экранах
    public void ApplySafeAreaInsets(double top, double bottom)
    {
        _lastSafeTop = top;
        _lastSafeBottom = bottom;
        ApplySidebarOuterMargin();
    }

    //переключение компактной плотности по высоте для альбомных телефонов и низких окон
    public void SetCompactHeightMode(bool isCompactHeight)
    {
        if (_isCompactHeight == isCompactHeight)
        {
            return;
        }

        _isCompactHeight = isCompactHeight;
        ApplySidebarOuterMargin();
        ApplyHeightDensity();
    }

    //применение внешних отступов панели с учетом компактного режима по высоте
    private void ApplySidebarOuterMargin()
    {
        var minVert = _isCompactHeight ? 8.0 : SideMargin;
        var extra = _isCompactHeight ? 4.0 : 8.0;
        var isMobile = DeviceInfo.Platform == DevicePlatform.Android || DeviceInfo.Platform == DevicePlatform.iOS;
        var topMargin = isMobile ? Math.Max(_lastSafeTop + extra, minVert) : minVert;
        var bottomMargin = isMobile ? Math.Max(_lastSafeBottom + extra, minVert) : minVert;
        DesktopSidebar.Margin = new Thickness(_isCompactHeight ? 8.0 : SideMargin, topMargin, 0, bottomMargin);
    }

    //адаптация высоты и состава пунктов панели чтобы иконки никогда не налезали друг на друга
    private void ApplyHeightDensity()
    {
        var compactRail = _isCompactHeight && !IsExpanded;
        var showSecondary = !compactRail;

        DesktopSidebar.Padding = _isCompactHeight ? new Thickness(8, 10) : new Thickness(10, 16);
        LogoRowGrid.HeightRequest = _isCompactHeight ? 34 : 42;
        LogoRowGrid.Margin = _isCompactHeight ? new Thickness(0, 0, 0, 8) : new Thickness(0, 0, 0, 20);
        NavItemsStack.Spacing = _isCompactHeight ? 2 : 4;

        var itemH = _isCompactHeight ? 36.0 : 42.0;
        SideNavVpn.HeightRequest = itemH;
        SideNavProfile.HeightRequest = itemH;
        SideNavConfiguration.HeightRequest = itemH;
        SideNavDevices.HeightRequest = itemH;
        SideNavSplit.HeightRequest = itemH;

        NavDivider.IsVisible = showSecondary;
        NavBug.IsVisible = showSecondary;
        NavReviews.IsVisible = showSecondary;
        VpnStatusPill.IsVisible = showSecondary;

        FooterStack.Margin = _isCompactHeight ? new Thickness(0, 6, 0, 0) : new Thickness(0, 12, 0, 0);
        FooterStack.Spacing = _isCompactHeight ? 4 : 8;
        LogoutButtonBorder.HeightRequest = _isCompactHeight ? 36 : 40;
    }

    //скрытие боковой панели
    public void HideSidebar() => DesktopSidebar.IsVisible = false;

    public event EventHandler<string>? NavTapped;
    public event EventHandler? LogoutTapped;
    public event EventHandler? SidebarWidthChanged;
    public event EventHandler<bool>? ExpandedChanged;

    //передача сервиса обновлений
    public void Initialize(IAppUpdaterService? appUpdaterService = null) =>
        _appUpdaterService = appUpdaterService;

    //обновление сезонной версии логотипа
    private void UpdateSeasonalLogo() =>
        AppLogoImage.Source = ThemeManager.Instance?.ActiveTheme?.Id.Contains("halloween", StringComparison.OrdinalIgnoreCase) == true
            ? "app_logo_halloween.png"
            : "app_logo.png";

    //анимация появления боковой панели
    public async Task PlayEntranceAnimationAsync()
    {
        DesktopSidebar.IsVisible = true;
        await UiAnimations.PlaySidebarEntranceAsync(
            DesktopSidebar,
            SideNavVpn,
            SideNavProfile,
            SideNavConfiguration,
            SideNavDevices,
            SideNavSplit,
            NavBug,
            NavReviews);
    }

    //индикация подключения vpn и цвета статуса
    public void UpdateVpnStatus(bool isConnected)
    {
        _isVpnConnected = isConnected;
        var customStatus = ThemeManager.Instance?.ActiveTheme?.Vfx?.StatusText;
        if (isConnected)
        {
            var success = GetResourceColor("Success", Color.FromArgb("#10B981"));
            var successDim = GetResourceColor("SuccessDim", success.WithAlpha(0.14f));
            VpnStatusDot.Color = success;
            VpnStatusLabel.Text = !string.IsNullOrWhiteSpace(customStatus?.Connected) ? customStatus.Connected : "Защищено";
            VpnStatusLabel.TextColor = success;
            VpnStatusPill.BackgroundColor = IsExpanded ? successDim : Colors.Transparent;
            VpnStatusPill.Stroke = IsExpanded ? success.WithAlpha(0.35f) : Colors.Transparent;
        }
        else
        {
            var muted = InactiveTextColor;
            VpnStatusDot.Color = muted;
            VpnStatusLabel.Text = !string.IsNullOrWhiteSpace(customStatus?.Disconnected) ? customStatus.Disconnected : "Отключен";
            VpnStatusLabel.TextColor = muted;
            VpnStatusPill.BackgroundColor = Colors.Transparent;
            VpnStatusPill.Stroke = Colors.Transparent;
        }
    }

    //актуализация цвета фона и границ под активную тему
    public void UpdateCardOpacity()
    {
        var bgSurface = GetResourceColor("BgSurface", Color.FromArgb("#161622"));
        var borderColor = _isOverlayMode
            ? GetResourceColor("BorderMedium", Color.FromArgb("#363650"))
            : GetResourceColor("BorderSubtle", Color.FromArgb("#28283C"));

        DesktopSidebar.BackgroundColor = bgSurface;
        DesktopSidebar.Stroke = borderColor;
    }

    //реакция на смену темы оформления
    public void OnThemeChanged() =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateSeasonalLogo();
            UpdateCardOpacity();
            UpdateActiveTab(_currentTab, animate: false);
            UpdateVpnStatus(_isVpnConnected);

            var textPrimary = GetResourceColor("TextPrimary", Colors.White);
            var textSecondary = GetResourceColor("TextSecondary", Color.FromArgb("#D0FFFFFF"));
            var textMuted = InactiveTextColor;
            var errorColor = GetResourceColor("Error", Color.FromArgb("#EF4444"));

            AppLogoText.TextColor = textPrimary;
            CollapseChevronIcon.IconColor = textMuted;
            LabelBug.TextColor = textSecondary;
            LabelReviews.TextColor = textSecondary;
            NavBugIcon.IconColor = textMuted;
            NavReviewsIcon.IconColor = textMuted;
            LogoutIcon.IconColor = errorColor;
            LogoutText.TextColor = errorColor;
            AppVersionLabel.TextColor = textMuted;
        });

    //разворачивание панели по клику в свернутом виде
    private void OnSidebarTapped(object? sender, TappedEventArgs e)
    {
        if (!IsExpanded)
        {
            ToggleSidebar();
        }
    }

    //сворачивание или разворачивание по нажатию на логотип
    private void OnLogoTapped(object? sender, TappedEventArgs e)
    {
        ThemeAudioService.PlayClick();
        ToggleSidebar();
    }

    //плавная анимация ширины панели без покадрового пересчета layout всей страницы
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "MAUI Animation is managed and executed by the visual element animation extension")]
    private void ToggleSidebar()
    {
        var generation = ++_toggleGeneration;
        IsExpanded = !IsExpanded;
        ApplyHeightDensity();
        ExpandedChanged?.Invoke(this, IsExpanded);
        var targetWidth = IsExpanded ? ExpandedWidth : CollapsedWidth;
        var currentWidth = DesktopSidebar.Width > 0 ? DesktopSidebar.Width : DesktopSidebar.WidthRequest;

        _ = this.AbortAnimation("SidebarResize");
        _ = this.AbortAnimation("SidebarLabelsAnim");
        AppLogoImage.CancelAnimations();

        if (IsExpanded)
        {
            AppLogoImage.Rotation = 0;
            _ = AppLogoImage.RotateToAsync(360, 220, Easing.CubicOut);
            VersionBadge.IsVisible = true;
            UpdateVpnStatus(_isVpnConnected);

            foreach (var label in _sidebarLabels)
            {
                label.IsVisible = true;
                label.Opacity = 0;
                label.TranslationX = -8;
            }

            AppVersionLabel.Opacity = 0;
            AppVersionLabel.TranslationX = -8;

            var labelsAnim = new Animation(v =>
            {
                var tx = -8.0 * (1.0 - v);
                foreach (var label in _sidebarLabels)
                {
                    label.Opacity = v;
                    label.TranslationX = tx;
                }

                AppVersionLabel.Opacity = v * 0.65;
                AppVersionLabel.TranslationX = tx;
            }, 0, 1);
            labelsAnim.Commit(this, "SidebarLabelsAnim", 16, 170, Easing.CubicOut);

            var anim = new Animation(v => DesktopSidebar.WidthRequest = v, currentWidth, targetWidth);
            anim.Commit(this, "SidebarResize", 16, 200, Easing.CubicOut, (_, _) =>
            {
                if (generation == _toggleGeneration && !_isOverlayMode)
                {
                    SidebarWidthChanged?.Invoke(this, EventArgs.Empty);
                }
            });
        }
        else
        {
            _ = AppLogoImage.RotateToAsync(0, 180, Easing.CubicInOut);
            UpdateVpnStatus(_isVpnConnected);

            var hideAnim = new Animation(v =>
            {
                var tx = -6.0 * (1.0 - v);
                foreach (var label in _sidebarLabels)
                {
                    label.Opacity = v;
                    label.TranslationX = tx;
                }

                AppVersionLabel.Opacity = v * 0.65;
                AppVersionLabel.TranslationX = tx;
            }, 1, 0);
            hideAnim.Commit(this, "SidebarLabelsAnim", 16, 90, Easing.CubicIn, (_, _) =>
            {
                if (generation == _toggleGeneration && !IsExpanded)
                {
                    HideSidebarLabels();
                }
            });

            var anim = new Animation(v => DesktopSidebar.WidthRequest = v, currentWidth, targetWidth);
            anim.Commit(this, "SidebarResize", 16, 180, Easing.CubicInOut, (_, _) =>
            {
                if (generation == _toggleGeneration && !IsExpanded)
                {
                    HideSidebarLabels();
                    if (!_isOverlayMode)
                    {
                        SidebarWidthChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
            });
        }
    }

    //скрытие текстовых подписей после сворачивания панели
    private void HideSidebarLabels()
    {
        foreach (var label in _sidebarLabels)
        {
            label.Opacity = 0;
            label.TranslationX = 0;
            label.IsVisible = false;
        }

        AppVersionLabel.Opacity = 0;
        AppVersionLabel.TranslationX = 0;
        VersionBadge.IsVisible = false;
    }

    //подсветка активной вкладки с fluent 2 индикатором и микро-анимацией иконки
    public void UpdateActiveTab(string tabName, bool animate = true)
    {
        var changed = !string.Equals(_currentTab, tabName, StringComparison.Ordinal);
        _currentTab = tabName;
        ResetAllSideNavItems();

        var activeColor = ActiveColor;
        var activeBg = ActiveBgColor;
        var textPrimary = GetResourceColor("TextPrimary", Colors.White);
        var boldFont = GetResourceFont("AppFontBold", "RobotoBold");
        var shouldAnimate = animate && changed;

        switch (tabName)
        {
            case "vpn":
                ApplyActiveNav(SideNavVpn, IndicatorVpn, NavVpnIcon, LabelVpn, activeBg, activeColor, textPrimary, boldFont);
                if (shouldAnimate)
                {
                    _ = NavVpnIcon.PlayIconPulseAsync(1.16);
                }

                break;
            case "profile":
                ApplyActiveNav(SideNavProfile, IndicatorProfile, NavProfileIcon, LabelProfile, activeBg, activeColor, textPrimary, boldFont);
                if (shouldAnimate)
                {
                    _ = NavProfileIcon.PlayIconBounceJumpAsync(-3);
                }

                break;
            case "configuration" or "themes":
                ApplyActiveNav(SideNavConfiguration, IndicatorConfiguration, NavConfigurationIcon, LabelConfiguration, activeBg, activeColor, textPrimary, boldFont);
                if (shouldAnimate)
                {
                    _ = NavConfigurationIcon.PlayIconSpinAsync(90, 200);
                }

                break;
            case "devices":
                ApplyActiveNav(SideNavDevices, IndicatorDevices, NavDevicesIcon, LabelDevices, activeBg, activeColor, textPrimary, boldFont);
                if (shouldAnimate)
                {
                    _ = NavDevicesIcon.PlayIconWiggleAsync(10);
                }

                break;
            case "split":
                ApplyActiveNav(SideNavSplit, IndicatorSplit, NavSplitIcon, LabelSplit, activeBg, activeColor, textPrimary, boldFont);
                if (shouldAnimate)
                {
                    _ = NavSplitIcon.PlayIconPulseAsync(1.16);
                }

                break;
        }
    }

    //применение активного состояния на элемент навигации без искажения масштаба
    private static void ApplyActiveNav(
        Border container,
        BoxView indicator,
        MauiIcon icon,
        Label label,
        Color activeBg,
        Color activeColor,
        Color textPrimary,
        string boldFont)
    {
        container.BackgroundColor = activeBg;
        indicator.Color = activeColor;
        indicator.IsVisible = true;
        icon.IconColor = activeColor;
        label.TextColor = textPrimary;
        label.FontFamily = boldFont;
    }

    //сброс визуального состояния всех пунктов меню
    private void ResetAllSideNavItems()
    {
        var inactiveColor = InactiveTextColor;
        var textSecondary = GetResourceColor("TextSecondary", Color.FromArgb("#D0FFFFFF"));
        var mediumFont = GetResourceFont("AppFontMedium", "RobotoMedium");

        ResetNavItem(SideNavVpn, IndicatorVpn, NavVpnIcon, LabelVpn, inactiveColor, textSecondary, mediumFont);
        ResetNavItem(SideNavProfile, IndicatorProfile, NavProfileIcon, LabelProfile, inactiveColor, textSecondary, mediumFont);
        ResetNavItem(SideNavConfiguration, IndicatorConfiguration, NavConfigurationIcon, LabelConfiguration, inactiveColor, textSecondary, mediumFont);
        ResetNavItem(SideNavDevices, IndicatorDevices, NavDevicesIcon, LabelDevices, inactiveColor, textSecondary, mediumFont);
        ResetNavItem(SideNavSplit, IndicatorSplit, NavSplitIcon, LabelSplit, inactiveColor, textSecondary, mediumFont);

        NavBug.BackgroundColor = Colors.Transparent;
        NavReviews.BackgroundColor = Colors.Transparent;
        LogoutButtonBorder.BackgroundColor = Colors.Transparent;
    }

    //сброс одного пункта меню
    private static void ResetNavItem(
        Border container,
        BoxView indicator,
        MauiIcon icon,
        Label label,
        Color inactiveIconColor,
        Color inactiveLabelColor,
        string mediumFont)
    {
        container.CancelAnimations();
        container.BackgroundColor = Colors.Transparent;
        container.Scale = 1.0;
        indicator.IsVisible = false;
        icon.CancelAnimations();
        icon.Rotation = 0;
        icon.Scale = 1.0;
        icon.TranslationY = 0;
        icon.IconColor = inactiveIconColor;
        label.TextColor = inactiveLabelColor;
        label.FontFamily = mediumFont;
    }

    //проверка активности пункта меню для hover-подсветки
    private bool IsNavItemActive(Border border) =>
        _currentTab == "vpn" && border == SideNavVpn ||
        _currentTab == "profile" && border == SideNavProfile ||
        _currentTab is "configuration" or "themes" && border == SideNavConfiguration ||
        _currentTab == "devices" && border == SideNavDevices ||
        _currentTab == "split" && border == SideNavSplit;

    //выбор вкладки vpn
    private void OnNavVpnTapped(object? sender, TappedEventArgs e)
    {
        ThemeAudioService.PlayClick();
        NavTapped?.Invoke(this, "vpn");
    }

    //выбор вкладки профиля
    private void OnNavProfileTapped(object? sender, TappedEventArgs e)
    {
        ThemeAudioService.PlayClick();
        NavTapped?.Invoke(this, "profile");
    }

    //выбор вкладки конфигурации
    private void OnNavConfigurationTapped(object? sender, TappedEventArgs e)
    {
        ThemeAudioService.PlayClick();
        NavTapped?.Invoke(this, "configuration");
    }

    //выбор вкладки устройств
    private void OnNavDevicesTapped(object? sender, TappedEventArgs e)
    {
        ThemeAudioService.PlayClick();
        NavTapped?.Invoke(this, "devices");
    }

    //выбор вкладки разделения трафика
    private void OnNavSplitTapped(object? sender, TappedEventArgs e)
    {
        ThemeAudioService.PlayClick();
        NavTapped?.Invoke(this, "split");
    }

    //переход в баг-трекер в системном браузере
    private void OnNavBugTapped(object? sender, TappedEventArgs e)
    {
        ThemeAudioService.PlayClick();
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
        LogoutTapped?.Invoke(this, EventArgs.Empty);
    }

    //деликатная подсветка при наведении курсора без скачков масштаба
    private void OnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Border border)
        {
            return;
        }

        ThemeAudioService.PlayHover();
        if (border == LogoutButtonBorder)
        {
            var errorDim = GetResourceColor("ErrorDim", Color.FromArgb("#1FEF4444"));
            border.BackgroundColor = errorDim;
            return;
        }

        if (!IsNavItemActive(border))
        {
            border.BackgroundColor = HoverBgColor;
        }
    }

    //возврат состояния после ухода курсора
    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is not Border border)
        {
            return;
        }

        border.BackgroundColor = IsNavItemActive(border) ? ActiveBgColor : Colors.Transparent;
    }

    //ручная проверка обновлений при клике на номер версии
    private async void OnVersionBadgeTappedAsync(object? sender, EventArgs e)
    {
        try
        {
            ThemeAudioService.PlayClick();
            var updater = _appUpdaterService ?? IPlatformApplication.Current?.Services.GetService<IAppUpdaterService>();
            if (updater is not null)
            {
                await updater.CheckForUpdatesAsync(true);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[UPDATE CHECK ERROR] {ex.Message}", ex);
        }
    }
}
