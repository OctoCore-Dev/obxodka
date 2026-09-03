namespace obxodka.Views;

public sealed partial class DesktopSidebarView : ContentView
{
    private static readonly Color t_cyanColor = Color.FromArgb("#00E5FF");
    private static readonly Color t_mutedColor = Color.FromArgb("#6A5A8A");
    private static readonly Color t_activeColor = Color.FromArgb("#7C3AED");
    private static readonly Color t_activeBgColor = Color.FromArgb("#1A7C3AED");
    private static readonly Color t_inactiveLightColor = Color.FromArgb("#9080B0");
    private static readonly Color t_inactiveDarkColor = Color.FromArgb("#6A5A8A");

    public event EventHandler<string>? NavTapped;
    public event EventHandler? LogoutTapped;
    private bool _isExpanded;

    public DesktopSidebarView()
    {
        InitializeComponent();
        AppVersionLabel.Text = $"v{AppInfo.Current.VersionString}";
    }

    public async Task PlayEntranceAnimationAsync()
    {
        DesktopSidebar.IsVisible = true;
        await UIAnimations.PlaySidebarEntranceAsync(
            DesktopSidebar,
            SideNavVpn,
            SideNavProfile,
            SideNavConfiguration,
            SideNavMesh,
            SideNavDevices,
            NavBug,
            NavReviews);
    }

    public void HideSidebar() => DesktopSidebar.IsVisible = false;

    public void UpdateVpnStatus(bool isConnected)
    {
        if (isConnected)
        {
            VpnStatusDot.Color = t_cyanColor;
            VpnStatusLabel.Text = "Защищено";
            VpnStatusLabel.TextColor = t_cyanColor;
        }
        else
        {
            VpnStatusDot.Color = t_mutedColor;
            VpnStatusLabel.Text = "Отключен";
            VpnStatusLabel.TextColor = t_mutedColor;
        }
    }

    private void OnSidebarTapped(object? sender, TappedEventArgs e)
    {
        if (!_isExpanded)
        {
            ToggleSidebar();
        }
    }

    private void OnLogoTapped(object? sender, TappedEventArgs e)
    {
        _ = AppLogoImage.BounceClickAsync();
        ToggleSidebar();
    }

    private void ToggleSidebar()
    {
        _isExpanded = !_isExpanded;
        var targetWidth = _isExpanded ? 260 : 84;

        if (!_isExpanded)
        {
            _ = AppLogoImage.RotateToAsync(0, 250, Easing.CubicInOut);

            _ = AnimateLabelHideAsync(AppLogoText);
            _ = AnimateLabelHideAsync(LabelVpn);
            _ = AnimateLabelHideAsync(LabelProfile);
            _ = AnimateLabelHideAsync(LabelConfiguration);
            _ = AnimateLabelHideAsync(LabelMesh);
            _ = AnimateLabelHideAsync(LabelDevices);
            _ = AnimateLabelHideAsync(LabelBug);
            _ = AnimateLabelHideAsync(LabelReviews);
            _ = AnimateLabelHideAsync(VpnStatusLabel);
            _ = AnimateLabelHideAsync(AppVersionLabel);
            _ = AnimateLabelHideAsync(LogoutText);
            _ = LogoutIcon.FadeToAsync(1, 150);

            var anim = new Animation(v => DesktopSidebar.WidthRequest = v, DesktopSidebar.Width, targetWidth);
            anim.Commit(this, "SidebarResize", 16, 250, Easing.CubicInOut);
        }
        else
        {
            _ = AppLogoImage.RotateToAsync(360, 300, Easing.CubicOut);

            var anim = new Animation(v => DesktopSidebar.WidthRequest = v, DesktopSidebar.Width, targetWidth);
            anim.Commit(this, "SidebarResize", 16, 250, Easing.CubicInOut, (v, c) =>
            {
                _ = AnimateLabelShowAsync(AppLogoText, 0);
                _ = AnimateLabelShowAsync(LabelVpn, 20);
                _ = AnimateLabelShowAsync(LabelProfile, 35);
                _ = AnimateLabelShowAsync(LabelConfiguration, 50);
                _ = AnimateLabelShowAsync(LabelMesh, 65);
                _ = AnimateLabelShowAsync(LabelDevices, 80);
                _ = AnimateLabelShowAsync(LabelBug, 95);
                _ = AnimateLabelShowAsync(LabelReviews, 110);
                _ = AnimateLabelShowAsync(VpnStatusLabel, 125);
                _ = AnimateLabelShowAsync(LogoutText, 140);
                _ = AnimateLabelShowAsync(AppVersionLabel, 155, 0.5);
                _ = LogoutIcon.FadeToAsync(0, 150);
            });
        }
    }

    private static async Task AnimateLabelShowAsync(VisualElement? label, int delayMs, double targetOpacity = 1.0)
    {
        if (label is null)
        {
            return;
        }

        if (delayMs > 0)
        {
            await Task.Delay(delayMs);
        }

        label.TranslationX = -10;
        label.Opacity = 0;
        _ = label.TranslateToAsync(0, 0, 200, Easing.SpringOut);
        _ = label.FadeToAsync(targetOpacity, 180, Easing.CubicOut);
    }

    private static async Task AnimateLabelHideAsync(VisualElement? label)
    {
        if (label is null)
        {
            return;
        }

        _ = label.TranslateToAsync(-10, 0, 120, Easing.CubicIn);
        _ = await label.FadeToAsync(0, 120, Easing.CubicIn);
        label.TranslationX = 0;
    }

    public void UpdateActiveTab(string tabName)
    {
        ResetAllSideNavItems();

        switch (tabName)
        {
            case "vpn":
                SideNavVpn.BackgroundColor = t_activeBgColor;
                NavVpnIcon.IconColor = t_activeColor;
                _ = SideNavVpn.ScaleToAsync(1.03, 150, Easing.SpringOut);
                _ = UIAnimations.PlayIconSpringHoverAsync(NavVpnIcon, 1.2);
                break;
            case "profile":
                SideNavProfile.BackgroundColor = t_activeBgColor;
                NavProfileIcon.IconColor = t_activeColor;
                _ = SideNavProfile.ScaleToAsync(1.03, 150, Easing.SpringOut);
                _ = UIAnimations.PlayIconBounceJumpAsync(NavProfileIcon, -3);
                break;
            case "configuration":
                SideNavConfiguration.BackgroundColor = t_activeBgColor;
                NavConfigurationIcon.IconColor = t_activeColor;
                _ = SideNavConfiguration.ScaleToAsync(1.03, 150, Easing.SpringOut);
                _ = UIAnimations.PlayIconSpinAsync(NavConfigurationIcon, 90, 200);
                break;
            case "mesh":
                SideNavMesh.BackgroundColor = t_activeBgColor;
                NavMeshIcon.IconColor = t_activeColor;
                _ = SideNavMesh.ScaleToAsync(1.03, 150, Easing.SpringOut);
                _ = UIAnimations.PlayIconPulseAsync(NavMeshIcon, 1.2);
                break;
            case "devices":
                SideNavDevices.BackgroundColor = t_activeBgColor;
                NavDevicesIcon.IconColor = t_activeColor;
                _ = SideNavDevices.ScaleToAsync(1.03, 150, Easing.SpringOut);
                _ = UIAnimations.PlayIconWiggleAsync(NavDevicesIcon, 12);
                break;
            default:
                break;
        }
    }

    private void ResetAllSideNavItems()
    {
        SideNavVpn.BackgroundColor = Colors.Transparent;
        SideNavProfile.BackgroundColor = Colors.Transparent;
        SideNavConfiguration.BackgroundColor = Colors.Transparent;
        SideNavMesh.BackgroundColor = Colors.Transparent;
        SideNavDevices.BackgroundColor = Colors.Transparent;

        SideNavVpn.Scale = 1.0;
        SideNavProfile.Scale = 1.0;
        SideNavConfiguration.Scale = 1.0;
        SideNavMesh.Scale = 1.0;
        SideNavDevices.Scale = 1.0;

        var inactiveColor = Application.Current?.RequestedTheme == AppTheme.Light
            ? t_inactiveLightColor
            : t_inactiveDarkColor;

        NavVpnIcon.IconColor = inactiveColor;
        NavProfileIcon.IconColor = inactiveColor;
        NavConfigurationIcon.IconColor = inactiveColor;
        NavMeshIcon.IconColor = inactiveColor;
        NavDevicesIcon.IconColor = inactiveColor;
    }

    private void OnNavVpnTapped(object? sender, TappedEventArgs e)
    {
        _ = SideNavVpn.BounceClickAsync();
        NavTapped?.Invoke(this, "vpn");
    }

    private void OnNavProfileTapped(object? sender, TappedEventArgs e)
    {
        _ = SideNavProfile.BounceClickAsync();
        NavTapped?.Invoke(this, "profile");
    }

    private void OnNavConfigurationTapped(object? sender, TappedEventArgs e)
    {
        _ = SideNavConfiguration.BounceClickAsync();
        NavTapped?.Invoke(this, "configuration");
    }

    private void OnNavMeshTapped(object? sender, TappedEventArgs e)
    {
        _ = SideNavMesh.BounceClickAsync();
        NavTapped?.Invoke(this, "mesh");
    }

    private void OnNavDevicesTapped(object? sender, TappedEventArgs e)
    {
        _ = SideNavDevices.BounceClickAsync();
        NavTapped?.Invoke(this, "devices");
    }

    private void OnNavBugTapped(object? sender, TappedEventArgs e)
    {
        _ = NavBug.BounceClickAsync();
        try
        {
            _ = Browser.Default.OpenAsync("https://obxodka.one/BugTracker/Index", BrowserLaunchMode.SystemPreferred);
        }
        catch { }
    }

    private void OnNavReviewsTapped(object? sender, TappedEventArgs e)
    {
        _ = NavReviews.BounceClickAsync();
        try
        {
            _ = Browser.Default.OpenAsync("https://obxodka.one/Reviews/Index", BrowserLaunchMode.SystemPreferred);
        }
        catch { }
    }

    private void OnLogoutClicked(object? sender, TappedEventArgs e)
    {
        _ = LogoutButtonBorder.BounceClickAsync();
        LogoutTapped?.Invoke(this, EventArgs.Empty);
    }

    private async void OnPointerEnteredAsync(object? sender, PointerEventArgs e)
    {
        if (sender is not Border border)
        {
            return;
        }

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
        else if (border == SideNavMesh)
        {
            await UIAnimations.PlayIconPulseAsync(NavMeshIcon, 1.25);
        }
        else if (border == SideNavDevices)
        {
            await UIAnimations.PlayIconWiggleAsync(NavDevicesIcon, 14);
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
        else if (border == SideNavMesh)
        {
            await UIAnimations.PlayIconHoverExitAsync(NavMeshIcon);
        }
        else if (border == SideNavDevices)
        {
            await UIAnimations.PlayIconHoverExitAsync(NavDevicesIcon);
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
}
