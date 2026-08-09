namespace obxodka.Views;

public partial class DesktopSidebarView : ContentView
{
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
            SideNavVpn, SideNavProfile, SideNavConfiguration, SideNavDevices, NavBug, NavReviews);
    }

    public void HideSidebar() => DesktopSidebar.IsVisible = false;
    public void UpdateVpnStatus(bool isConnected)
    {
        if (isConnected)
        {
            VpnStatusDot.Color = Color.FromArgb("#00E5FF");
            VpnStatusLabel.Text = "Защищено";
            VpnStatusLabel.TextColor = Color.FromArgb("#00E5FF");
        }
        else
        {
            VpnStatusDot.Color = Color.FromArgb("#6A5A8A");
            VpnStatusLabel.Text = "Отключен";
            VpnStatusLabel.TextColor = Color.FromArgb("#6A5A8A");
        }
    }
    private void OnSidebarTapped(object sender, TappedEventArgs e)
    {
        if (!_isExpanded)
        {
            ToggleSidebar();
        }
    }

    private void OnLogoTapped(object sender, TappedEventArgs e) => ToggleSidebar();
    private void ToggleSidebar()
    {
        _isExpanded = !_isExpanded;
        var targetWidth = _isExpanded ? 260 : 70;
        if (!_isExpanded)
        {
            _ = AppLogoText.FadeToAsync(0, 150);
            _ = LabelVpn.FadeToAsync(0, 150);
            _ = LabelProfile.FadeToAsync(0, 150);
            _ = LabelConfiguration.FadeToAsync(0, 150);
            _ = LabelDevices.FadeToAsync(0, 150);
            _ = LabelBug.FadeToAsync(0, 150);
            _ = LabelReviews.FadeToAsync(0, 150);
            _ = VpnStatusLabel.FadeToAsync(0, 150);
            _ = AppVersionLabel.FadeToAsync(0, 150);
            _ = LogoutText.FadeToAsync(0, 150);
            _ = LogoutIcon.FadeToAsync(1, 150);
            var anim = new Animation(v => DesktopSidebar.WidthRequest = v, DesktopSidebar.Width, targetWidth);
            anim.Commit(this, "SidebarResize", 16, 250, Easing.CubicInOut);
        }
        else
        {
            var anim = new Animation(v => DesktopSidebar.WidthRequest = v, DesktopSidebar.Width, targetWidth);
            anim.Commit(this, "SidebarResize", 16, 250, Easing.CubicInOut, (v, c) => Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(50), () =>
                {
                    _ = AppLogoText.FadeToAsync(1, 150);
                    _ = LabelVpn.FadeToAsync(1, 150);
                    _ = LabelProfile.FadeToAsync(1, 150);
                    _ = LabelConfiguration.FadeToAsync(1, 150);
                    _ = LabelDevices.FadeToAsync(1, 150);
                    _ = LabelBug.FadeToAsync(1, 150);
                    _ = LabelReviews.FadeToAsync(1, 150);
                    _ = VpnStatusLabel.FadeToAsync(1, 150);
                    _ = AppVersionLabel.FadeToAsync(0.5, 150);
                    _ = LogoutText.FadeToAsync(1, 150);
                    _ = LogoutIcon.FadeToAsync(0, 150);
                }));
        }
    }

    public void UpdateActiveTab(string tabName)
    {
        ResetAllSideNavItems();
        var activeColor = Color.FromArgb("#7C3AED");
        switch (tabName)
        {
            case "vpn":
                SideNavVpn.BackgroundColor = Color.FromArgb("#1A7C3AED");
                NavVpnIcon.IconColor = activeColor;
                break;
            case "profile":
                SideNavProfile.BackgroundColor = Color.FromArgb("#1A7C3AED");
                NavProfileIcon.IconColor = activeColor;
                break;
            case "configuration":
                SideNavConfiguration.BackgroundColor = Color.FromArgb("#1A7C3AED");
                NavConfigurationIcon.IconColor = activeColor;
                break;
            case "devices":
                SideNavDevices.BackgroundColor = Color.FromArgb("#1A7C3AED");
                NavDevicesIcon.IconColor = activeColor;
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
        SideNavDevices.BackgroundColor = Colors.Transparent;

        var inactiveColor = Application.Current?.RequestedTheme == AppTheme.Light
            ? Color.FromArgb("#9080B0")
            : Color.FromArgb("#6A5A8A");

        NavVpnIcon.IconColor = inactiveColor;
        NavProfileIcon.IconColor = inactiveColor;
        NavConfigurationIcon.IconColor = inactiveColor;
        NavDevicesIcon.IconColor = inactiveColor;
    }

    private void OnNavVpnTapped(object sender, TappedEventArgs e) => NavTapped?.Invoke(this, "vpn");
    private void OnNavProfileTapped(object sender, TappedEventArgs e) => NavTapped?.Invoke(this, "profile");
    private void OnNavConfigurationTapped(object sender, TappedEventArgs e) => NavTapped?.Invoke(this, "configuration");
    private void OnNavDevicesTapped(object sender, TappedEventArgs e) => NavTapped?.Invoke(this, "devices");

    private void OnNavBugTapped(object sender, TappedEventArgs e)
    {
        try
        { _ = Browser.Default.OpenAsync("https://obxodka.one/BugTracker/Index", BrowserLaunchMode.SystemPreferred); }
        catch { }
    }

    private void OnNavReviewsTapped(object sender, TappedEventArgs e)
    {
        try
        { _ = Browser.Default.OpenAsync("https://obxodka.one/Reviews/Index", BrowserLaunchMode.SystemPreferred); }
        catch { }
    }

    private void OnLogoutClickedAsync(object sender, TappedEventArgs e) => LogoutTapped?.Invoke(this, EventArgs.Empty);
}

