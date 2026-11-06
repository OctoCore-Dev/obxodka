namespace obxodka.Pages;

//нижняя плавающая панель навигации для мобильных устройств
public sealed partial class MobileSidebarView : ContentView
{
    private static Color ActiveColor => (Application.Current?.Resources.TryGetValue("Primary", out var val) == true && val is Color c)
        ? c
        : Color.FromArgb("#7C3AED");
    private static readonly Color s_inactiveLightColor = Color.FromArgb("#9080B0");
    private static readonly Color s_inactiveDarkColor = Color.FromArgb("#6A5A8A");

    public event EventHandler<string>? NavTapped;
    private string _currentTab = "vpn";
    private bool _isCompact;

    public MobileSidebarView() => InitializeComponent();

    //анимация появления нижней панели
    public async Task PlayEntranceAnimationAsync()
    {
        MobileBottomBar.IsVisible = true;
        await UIAnimations.PlayBottomBarEntranceAsync(MobileBottomBar);
    }

    //скрытие нижней панели
    public void HideSidebar() => MobileBottomBar.IsVisible = false;

    //переключение компактного режима для коротких экранов
    public void SetCompactMode(bool isCompact)
    {
        _isCompact = isCompact;
        ApplyLayoutMetrics();
    }

    //обновление нижнего отступа с учетом системной жестовой панели
    public void ApplyBottomInset(double bottomInset) =>
        ApplyLayoutMetrics(bottomInset);

    //применение геометрических параметров панели
    private void ApplyLayoutMetrics(double? overrideBottom = null)
    {
        var bottomSafe = overrideBottom ?? SafeAreaHelper.BottomInset;

        if (_isCompact)
        {
            MobileBottomBar.HeightRequest = 52;
            MobileBottomBar.Margin = new Thickness(12, 0, 12, Math.Max(8, bottomSafe + 6));
            MobileBottomBar.Padding = new Thickness(4, 2);
        }
        else
        {
            MobileBottomBar.HeightRequest = 68;
            MobileBottomBar.Margin = new Thickness(16, 0, 16, Math.Max(16, bottomSafe + 8));
            MobileBottomBar.Padding = new Thickness(6, 6);
        }
    }

    //обновление визуального состояния активной вкладки
    public void UpdateActiveTab(string tabName)
    {
        _currentTab = tabName;
        ResetAllBottomNavItems();

        var activeColor = ActiveColor;
        var activeBg = Colors.Transparent;

        switch (tabName)
        {
            case "vpn":
                BottomNavVpn.BackgroundColor = activeBg;
                BottomNavVpnIcon.IconColor = activeColor;
                PillVpn.Color = activeColor;
                _ = BottomNavVpn.ScaleToAsync(1.04, 140, Easing.SpringOut);
                _ = UIAnimations.ShowPillAsync(PillVpn);
                _ = UIAnimations.PlayIconSpringHoverAsync(BottomNavVpnIcon, 1.22);
                break;
            case "configuration":
            case "themes":
                BottomNavConfiguration.BackgroundColor = activeBg;
                BottomNavConfigurationIcon.IconColor = activeColor;
                PillBattery.Color = activeColor;
                _ = BottomNavConfiguration.ScaleToAsync(1.04, 140, Easing.SpringOut);
                _ = UIAnimations.ShowPillAsync(PillBattery);
                _ = UIAnimations.PlayIconSpinAsync(BottomNavConfigurationIcon, 180, 260);
                break;
            case "profile":
                BottomNavProfile.BackgroundColor = activeBg;
                BottomNavProfileIcon.IconColor = activeColor;
                PillProfile.Color = activeColor;
                _ = BottomNavProfile.ScaleToAsync(1.04, 140, Easing.SpringOut);
                _ = UIAnimations.ShowPillAsync(PillProfile);
                _ = UIAnimations.PlayIconBounceJumpAsync(BottomNavProfileIcon, -4);
                break;
            case "devices":
                BottomNavDevices.BackgroundColor = activeBg;
                BottomNavDevicesIcon.IconColor = activeColor;
                PillDevices.Color = activeColor;
                _ = BottomNavDevices.ScaleToAsync(1.04, 140, Easing.SpringOut);
                _ = UIAnimations.ShowPillAsync(PillDevices);
                _ = UIAnimations.PlayIconWiggleAsync(BottomNavDevicesIcon, 14);
                break;
            case "split":
                BottomNavSplit.BackgroundColor = activeBg;
                BottomNavSplitIcon.IconColor = activeColor;
                PillSplit.Color = activeColor;
                _ = BottomNavSplit.ScaleToAsync(1.04, 140, Easing.SpringOut);
                _ = UIAnimations.ShowPillAsync(PillSplit);
                _ = UIAnimations.PlayIconTwinkleAsync(BottomNavSplitIcon);
                break;
            default:
                break;
        }
    }

    //сброс активных индикаторов и цветов элементов
    private void ResetAllBottomNavItems()
    {
        _ = UIAnimations.HidePillAsync(PillVpn);
        _ = UIAnimations.HidePillAsync(PillBattery);
        _ = UIAnimations.HidePillAsync(PillProfile);
        _ = UIAnimations.HidePillAsync(PillDevices);
        _ = UIAnimations.HidePillAsync(PillSplit);

        var activeColor = ActiveColor;
        PillVpn.Color = activeColor;
        PillBattery.Color = activeColor;
        PillProfile.Color = activeColor;
        PillDevices.Color = activeColor;
        PillSplit.Color = activeColor;

        BottomNavVpn.BackgroundColor = Colors.Transparent;
        BottomNavConfiguration.BackgroundColor = Colors.Transparent;
        BottomNavProfile.BackgroundColor = Colors.Transparent;
        BottomNavDevices.BackgroundColor = Colors.Transparent;
        BottomNavSplit.BackgroundColor = Colors.Transparent;

        BottomNavVpn.Scale = 1.0;
        BottomNavConfiguration.Scale = 1.0;
        BottomNavProfile.Scale = 1.0;
        BottomNavDevices.Scale = 1.0;
        BottomNavSplit.Scale = 1.0;

        var inactiveColor = (Application.Current?.Resources.TryGetValue("TextMuted", out var tm) == true && tm is Color tmc)
            ? tmc
            : (Application.Current?.RequestedTheme == AppTheme.Light ? s_inactiveLightColor : s_inactiveDarkColor);

        BottomNavVpnIcon.IconColor = inactiveColor;
        BottomNavConfigurationIcon.IconColor = inactiveColor;
        BottomNavProfileIcon.IconColor = inactiveColor;
        BottomNavDevicesIcon.IconColor = inactiveColor;
        BottomNavSplitIcon.IconColor = inactiveColor;

        ResetIconTransform(BottomNavVpnIcon);
        ResetIconTransform(BottomNavConfigurationIcon);
        ResetIconTransform(BottomNavProfileIcon);
        ResetIconTransform(BottomNavDevicesIcon);
        ResetIconTransform(BottomNavSplitIcon);
    }

    //актуализация цвета фона и границы панели под тему
    public void UpdateCardOpacity()
    {
        var bgElevated = (Application.Current?.Resources.TryGetValue("BgElevated", out var bg) == true && bg is Color bgColor)
            ? bgColor
            : Color.FromArgb("#202030");
        var borderMedium = (Application.Current?.Resources.TryGetValue("BorderMedium", out var bm) == true && bm is Color bmColor)
            ? bmColor
            : Color.FromArgb("#363650");

        MobileBottomBar.BackgroundColor = bgElevated;
        MobileBottomBar.Stroke = borderMedium;
    }

    //реакция на смену темы оформления
    public void OnThemeChanged() =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateCardOpacity();
            UpdateActiveTab(_currentTab);
        });

    //сброс трансформаций и анимаций иконки
    private static void ResetIconTransform(VisualElement icon)
    {
        icon.CancelAnimations();
        icon.Rotation = 0;
        icon.TranslationX = 0;
        icon.TranslationY = 0;
        _ = icon.ScaleToAsync(1.0, 100, Easing.CubicOut);
    }

    //выбор вкладки vpn
    private void OnNavVpnTapped(object? sender, TappedEventArgs e)
    {
        ThemeAudioService.PlayClick();
        _ = BottomNavVpn.BounceClickAsync();
        NavTapped?.Invoke(this, "vpn");
    }

    //выбор вкладки профиля
    private void OnNavProfileTapped(object? sender, TappedEventArgs e)
    {
        ThemeAudioService.PlayClick();
        _ = BottomNavProfile.BounceClickAsync();
        NavTapped?.Invoke(this, "profile");
    }

    //выбор вкладки устройств
    private void OnNavDevicesTapped(object? sender, TappedEventArgs e)
    {
        ThemeAudioService.PlayClick();
        _ = BottomNavDevices.BounceClickAsync();
        NavTapped?.Invoke(this, "devices");
    }

    //выбор вкладки разделения трафика
    private void OnNavSplitTapped(object? sender, TappedEventArgs e)
    {
        ThemeAudioService.PlayClick();
        _ = BottomNavSplit.BounceClickAsync();
        NavTapped?.Invoke(this, "split");
    }

    //выбор вкладки настроек
    private void OnNavConfigurationTapped(object? sender, TappedEventArgs e)
    {
        ThemeAudioService.PlayClick();
        _ = BottomNavConfiguration.BounceClickAsync();
        NavTapped?.Invoke(this, "configuration");
    }
}
