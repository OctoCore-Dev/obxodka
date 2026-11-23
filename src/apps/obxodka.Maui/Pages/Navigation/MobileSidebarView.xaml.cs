namespace obxodka.Pages.Navigation;

//нижняя плавающая панель навигации в стиле material 3 adaptive для мобильных экранов
public sealed partial class MobileSidebarView
{
    private string _currentTab = "vpn";
    private bool _isCompact;

    public MobileSidebarView() => InitializeComponent();

    private static Color GetResourceColor(string key, Color fallback) =>
        Application.Current?.Resources.TryGetValue(key, out var val) == true && val is Color c ? c : fallback;

    private static string GetResourceFont(string key, string fallback) =>
        Application.Current?.Resources.TryGetValue(key, out var val) == true && val is string s && !string.IsNullOrWhiteSpace(s)
            ? s
            : fallback;

    private static Color ActiveColor => GetResourceColor("Primary", Color.FromArgb("#0078D4"));

    private static Color ActivePillColor => ActiveColor.WithAlpha(0.18f);

    private static Color InactiveColor => GetResourceColor("TextMuted", Color.FromArgb("#90FFFFFF"));

    public event EventHandler<string>? NavTapped;

    //анимация появления нижней панели
    public async Task PlayEntranceAnimationAsync()
    {
        MobileBottomBar.IsVisible = true;
        await UiAnimations.PlayBottomBarEntranceAsync(MobileBottomBar);
    }

    //реакция на смену темы оформления
    public void OnThemeChanged() =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateCardOpacity();
            UpdateActiveTab(_currentTab);
        });

    //скрытие нижней панели
    public void HideSidebar() => MobileBottomBar.IsVisible = false;

    //переключение компактного режима для коротких горизонтальных экранов
    public void SetCompactMode(bool isCompact)
    {
        if (_isCompact == isCompact)
        {
            return;
        }

        _isCompact = isCompact;
        ApplyLayoutMetrics();
    }

    //обновление нижнего отступа с учетом системной жестовой панели
    public void ApplyBottomInset(double bottomInset) =>
        ApplyLayoutMetrics(bottomInset);

    //адаптация выравнивания и отступов панели под узкие и широкие мобильные экраны
    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width <= 0 || MobileBottomBar is null)
        {
            return;
        }

        var useCenteredCapsule = width > 488.0;
        MobileBottomBar.HorizontalOptions = useCenteredCapsule ? LayoutOptions.Center : LayoutOptions.Fill;
        if (useCenteredCapsule)
        {
            MobileBottomBar.WidthRequest = 460.0;
        }
        else if (MobileBottomBar.WidthRequest > 0)
        {
            MobileBottomBar.WidthRequest = -1;
        }
    }

    //применение геометрических параметров панели
    private void ApplyLayoutMetrics(double? overrideBottom = null)
    {
        var bottomSafe = overrideBottom ?? SafeAreaHelper.BottomInset;
        var showLabels = !_isCompact;

        LabelVpn.IsVisible = showLabels;
        LabelProfile.IsVisible = showLabels;
        LabelDevices.IsVisible = showLabels;
        LabelSplit.IsVisible = showLabels;
        LabelConfiguration.IsVisible = showLabels;

        if (_isCompact)
        {
            MobileBottomBar.HeightRequest = 50;
            MobileBottomBar.Margin = new Thickness(12, 0, 12, Math.Max(8, bottomSafe + 4));
            MobileBottomBar.Padding = new Thickness(6, 4);
        }
        else
        {
            MobileBottomBar.HeightRequest = 66;
            MobileBottomBar.Margin = new Thickness(14, 0, 14, Math.Max(14, bottomSafe + 6));
            MobileBottomBar.Padding = new Thickness(8, 6);
        }
    }

    //обновление визуального состояния активной вкладки с material 3 пилюлей и микро-анимациями иконок
    public void UpdateActiveTab(string tabName)
    {
        _currentTab = tabName;
        ResetAllBottomNavItems();

        var activeColor = ActiveColor;
        var activePill = ActivePillColor;
        var textPrimary = GetResourceColor("TextPrimary", Colors.White);
        var boldFont = GetResourceFont("AppFontBold", "RobotoBold");

        switch (tabName)
        {
            case "vpn":
                ApplyActiveItem(BottomNavVpn, PillVpn, BottomNavVpnIcon, LabelVpn, activePill, activeColor, textPrimary, boldFont);
                _ = BottomNavVpnIcon.PlayIconSpringHoverAsync(1.22);
                break;
            case "configuration" or "themes":
                ApplyActiveItem(BottomNavConfiguration, PillConfiguration, BottomNavConfigurationIcon, LabelConfiguration, activePill, activeColor, textPrimary, boldFont);
                _ = BottomNavConfigurationIcon.PlayIconSpinAsync(180, 260);
                break;
            case "profile":
                ApplyActiveItem(BottomNavProfile, PillProfile, BottomNavProfileIcon, LabelProfile, activePill, activeColor, textPrimary, boldFont);
                _ = BottomNavProfileIcon.PlayIconBounceJumpAsync();
                break;
            case "devices":
                ApplyActiveItem(BottomNavDevices, PillDevices, BottomNavDevicesIcon, LabelDevices, activePill, activeColor, textPrimary, boldFont);
                _ = BottomNavDevicesIcon.PlayIconWiggleAsync();
                break;
            case "split":
                ApplyActiveItem(BottomNavSplit, PillSplit, BottomNavSplitIcon, LabelSplit, activePill, activeColor, textPrimary, boldFont);
                _ = BottomNavSplitIcon.PlayIconTwinkleAsync();
                break;
        }
    }

    //применение активного состояния пилюли и подписи
    private static void ApplyActiveItem(
        Border item,
        Border pill,
        MauiIcon icon,
        Label label,
        Color pillColor,
        Color activeColor,
        Color textPrimary,
        string boldFont)
    {
        pill.CancelAnimations();
        pill.BackgroundColor = pillColor;
        pill.Scale = 0.86;
        _ = pill.ScaleToAsync(1.0, 160, Easing.SpringOut);
        _ = item.ScaleToAsync(1.04, 140, Easing.SpringOut);
        icon.IconColor = activeColor;
        label.TextColor = textPrimary;
        label.FontFamily = boldFont;
    }

    //сброс активных индикаторов и цветов элементов
    private void ResetAllBottomNavItems()
    {
        var inactive = InactiveColor;
        var mediumFont = GetResourceFont("AppFontMedium", "RobotoMedium");

        ResetNavItem(BottomNavVpn, PillVpn, BottomNavVpnIcon, LabelVpn, inactive, mediumFont);
        ResetNavItem(BottomNavConfiguration, PillConfiguration, BottomNavConfigurationIcon, LabelConfiguration, inactive, mediumFont);
        ResetNavItem(BottomNavProfile, PillProfile, BottomNavProfileIcon, LabelProfile, inactive, mediumFont);
        ResetNavItem(BottomNavDevices, PillDevices, BottomNavDevicesIcon, LabelDevices, inactive, mediumFont);
        ResetNavItem(BottomNavSplit, PillSplit, BottomNavSplitIcon, LabelSplit, inactive, mediumFont);
    }

    //сброс одного пункта нижней навигации
    private static void ResetNavItem(Border item, Border pill, MauiIcon icon, Label label, Color inactiveColor, string mediumFont)
    {
        item.CancelAnimations();
        item.Scale = 1.0;
        pill.CancelAnimations();
        pill.Scale = 1.0;
        pill.BackgroundColor = Colors.Transparent;
        icon.CancelAnimations();
        icon.Rotation = 0;
        icon.Scale = 1.0;
        icon.TranslationY = 0;
        icon.IconColor = inactiveColor;
        label.TextColor = inactiveColor;
        label.FontFamily = mediumFont;
    }

    //актуализация цвета фона и границы панели под активную тему и cardopacity
    public void UpdateCardOpacity()
    {
        var bgSurface = GetResourceColor("BgSurface", Color.FromArgb("#161622"));
        var borderMedium = GetResourceColor("BorderMedium", Color.FromArgb("#363650"));

        MobileBottomBar.BackgroundColor = bgSurface;
        MobileBottomBar.Stroke = borderMedium;
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
