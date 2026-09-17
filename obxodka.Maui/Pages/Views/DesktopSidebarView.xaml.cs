namespace obxodka.Views;

public sealed partial class DesktopSidebarView : ContentView
{
    private static readonly Color t_cyanColor = Color.FromArgb("#00E5FF");
    private static readonly Color t_mutedColor = Color.FromArgb("#6A5A8A");
    private static Color ActiveColor => (Application.Current?.Resources.TryGetValue("Primary", out var val) == true && val is Color c)
        ? c
        : Color.FromArgb("#7C3AED");
    private static Color ActiveBgColor => ActiveColor.WithAlpha(0.14f);
    private static readonly Color t_inactiveLightColor = Color.FromArgb("#9080B0");
    private static readonly Color t_inactiveDarkColor = Color.FromArgb("#6A5A8A");

    public event EventHandler<string>? NavTapped;
    public event EventHandler? LogoutTapped;
    private bool _isExpanded;
    private string _currentTab = "vpn";

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
            var accent = (Application.Current?.Resources.TryGetValue("Accent", out var a) == true && a is Color ac) ? ac : t_cyanColor;
            VpnStatusDot.Color = accent;
            VpnStatusLabel.Text = "Защищено";
            VpnStatusLabel.TextColor = accent;
        }
        else
        {
            var muted = (Application.Current?.Resources.TryGetValue("TextMuted", out var m) == true && m is Color mc) ? mc : t_mutedColor;
            VpnStatusDot.Color = muted;
            VpnStatusLabel.Text = "Отключен";
            VpnStatusLabel.TextColor = muted;
        }
    }

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

    public void OnThemeChanged() =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateCardOpacity();
            UpdateActiveTab(_currentTab);
        });

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
            _ = AppLogoImage.RotateToAsync(0, 200, Easing.CubicInOut);

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
            _ = LogoutIcon.FadeToAsync(1, 140);

            var anim = new Animation(v => DesktopSidebar.WidthRequest = v, DesktopSidebar.Width, targetWidth);
            anim.Commit(this, "SidebarResize", 16, 200, Easing.CubicInOut);
        }
        else
        {
            _ = AppLogoImage.RotateToAsync(360, 240, Easing.CubicOut);

            _ = AnimateLabelShowAsync(AppLogoText, 10);
            _ = AnimateLabelShowAsync(LabelVpn, 25);
            _ = AnimateLabelShowAsync(LabelProfile, 40);
            _ = AnimateLabelShowAsync(LabelConfiguration, 55);
            _ = AnimateLabelShowAsync(LabelMesh, 70);
            _ = AnimateLabelShowAsync(LabelDevices, 85);
            _ = AnimateLabelShowAsync(LabelBug, 100);
            _ = AnimateLabelShowAsync(LabelReviews, 115);
            _ = AnimateLabelShowAsync(VpnStatusLabel, 125);
            _ = AnimateLabelShowAsync(LogoutText, 135);
            _ = AnimateLabelShowAsync(AppVersionLabel, 145, 0.5);
            _ = LogoutIcon.FadeToAsync(0, 120);

            var anim = new Animation(v => DesktopSidebar.WidthRequest = v, DesktopSidebar.Width, targetWidth);
            anim.Commit(this, "SidebarResize", 16, 220, Easing.CubicOut);
        }
    }

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
            case "mesh":
                SideNavMesh.BackgroundColor = ActiveBgColor;
                NavMeshIcon.IconColor = ActiveColor;
                _ = SideNavMesh.ScaleToAsync(1.03, 150, Easing.SpringOut);
                _ = UIAnimations.PlayIconPulseAsync(NavMeshIcon, 1.2);
                break;
            case "devices":
                SideNavDevices.BackgroundColor = ActiveBgColor;
                NavDevicesIcon.IconColor = ActiveColor;
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

        var inactiveColor = (Application.Current?.Resources.TryGetValue("TextMuted", out var tm) == true && tm is Color tmc)
            ? tmc
            : (Application.Current?.RequestedTheme == AppTheme.Light ? t_inactiveLightColor : t_inactiveDarkColor);

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

    private async void OnVersionBadgeTappedAsync(object? sender, EventArgs e)
    {
        if (sender is VisualElement ve)
        {
            _ = ve.BounceClickAsync();
        }

        var updater = IPlatformApplication.Current?.Services?.GetService<IAppUpdaterService>();
        if (updater is not null)
        {
            await updater.CheckForUpdatesAsync(manualCheck: true);
        }
    }
}
