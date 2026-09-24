namespace obxodka.Views;

public sealed partial class MobileSidebarView : ContentView
{
    private static Color ActiveColor => (Application.Current?.Resources.TryGetValue("Primary", out var val) == true && val is Color c)
        ? c
        : Color.FromArgb("#7C3AED");
    private static readonly Color t_inactiveLightColor = Color.FromArgb("#9080B0");
    private static readonly Color t_inactiveDarkColor = Color.FromArgb("#6A5A8A");

    public event EventHandler<string>? NavTapped;
    private string _currentTab = "vpn";

    public MobileSidebarView() => InitializeComponent();

    public async Task PlayEntranceAnimationAsync()
    {
        MobileBottomBar.IsVisible = true;
        await UIAnimations.PlayBottomBarEntranceAsync(MobileBottomBar);
    }

    public void HideSidebar() => MobileBottomBar.IsVisible = false;

    public void SetCompactMode(bool isCompact)
    {
        if (isCompact)
        {
            MobileBottomBar.HeightRequest = 52;
            MobileBottomBar.Margin = new Thickness(12, 0, 12, 8);
            MobileBottomBar.Padding = new Thickness(4, 2);
        }
        else
        {
            MobileBottomBar.HeightRequest = 68;
            MobileBottomBar.Margin = new Thickness(16, 0, 16, 20);
            MobileBottomBar.Padding = new Thickness(6, 6);
        }
    }

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
            : (Application.Current?.RequestedTheme == AppTheme.Light ? t_inactiveLightColor : t_inactiveDarkColor);

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

    public void OnThemeChanged() =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateCardOpacity();
            UpdateActiveTab(_currentTab);
        });

    private static void ResetIconTransform(VisualElement icon)
    {
        icon.CancelAnimations();
        icon.Rotation = 0;
        icon.TranslationX = 0;
        icon.TranslationY = 0;
        _ = icon.ScaleToAsync(1.0, 100, Easing.CubicOut);
    }

    private void OnNavVpnTapped(object? sender, TappedEventArgs e)
    {
        _ = BottomNavVpn.BounceClickAsync();
        NavTapped?.Invoke(this, "vpn");
    }

    private void OnNavProfileTapped(object? sender, TappedEventArgs e)
    {
        _ = BottomNavProfile.BounceClickAsync();
        NavTapped?.Invoke(this, "profile");
    }

    private void OnNavDevicesTapped(object? sender, TappedEventArgs e)
    {
        _ = BottomNavDevices.BounceClickAsync();
        NavTapped?.Invoke(this, "devices");
    }

    private void OnNavSplitTapped(object? sender, TappedEventArgs e)
    {
        _ = BottomNavSplit.BounceClickAsync();
        NavTapped?.Invoke(this, "split");
    }

    private void OnNavConfigurationTapped(object? sender, TappedEventArgs e)
    {
        _ = BottomNavConfiguration.BounceClickAsync();
        NavTapped?.Invoke(this, "configuration");
    }
}
