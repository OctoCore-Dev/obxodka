namespace obxodka.Views;

public partial class MobileSidebarView : ContentView
{
    public event EventHandler<string>? NavTapped;

    public MobileSidebarView() => InitializeComponent();

    public async Task PlayEntranceAnimationAsync()
    {
        MobileBottomBar.IsVisible = true;
        await UIAnimations.PlayBottomBarEntranceAsync(MobileBottomBar);
    }

    public void HideSidebar() => MobileBottomBar.IsVisible = false;

    public void UpdateActiveTab(string tabName)
    {
        ResetAllBottomNavItems();
        var activeColor = Color.FromArgb("#7C3AED");
        switch (tabName)
        {
            case "vpn":
                BottomNavVpnIcon.IconColor = activeColor;
                PillVpn.IsVisible = true;
                _ = PillVpn.FadeToAsync(1, 150);
                _ = PillVpn.ScaleToAsync(1, 150, Easing.SpringOut);
                break;
            case "profile":
                BottomNavProfileIcon.IconColor = activeColor;
                PillProfile.IsVisible = true;
                _ = PillProfile.FadeToAsync(1, 150);
                _ = PillProfile.ScaleToAsync(1, 150, Easing.SpringOut);
                break;
            case "devices":
                BottomNavDevicesIcon.IconColor = activeColor;
                PillDevices.IsVisible = true;
                _ = PillDevices.FadeToAsync(1, 150);
                _ = PillDevices.ScaleToAsync(1, 150, Easing.SpringOut);
                break;
            case "split":
                BottomNavSplitIcon.IconColor = activeColor;
                PillSplit.IsVisible = true;
                _ = PillSplit.FadeToAsync(1, 150);
                _ = PillSplit.ScaleToAsync(1, 150, Easing.SpringOut);
                break;
            default:
                break;
        }
    }

    private void ResetAllBottomNavItems()
    {
        PillVpn.Opacity = 0;
        PillVpn.Scale = 0.5;
        PillVpn.IsVisible = false;
        PillProfile.Opacity = 0;
        PillProfile.Scale = 0.5;
        PillProfile.IsVisible = false;
        PillDevices.Opacity = 0;
        PillDevices.Scale = 0.5;
        PillDevices.IsVisible = false;
        PillSplit.Opacity = 0;
        PillSplit.Scale = 0.5;
        PillSplit.IsVisible = false;

        var inactiveColor = Application.Current?.RequestedTheme == AppTheme.Light
            ? Color.FromArgb("#9080B0")
            : Color.FromArgb("#6A5A8A");

        BottomNavVpnIcon.IconColor = inactiveColor;
        BottomNavProfileIcon.IconColor = inactiveColor;
        BottomNavDevicesIcon.IconColor = inactiveColor;
        BottomNavSplitIcon.IconColor = inactiveColor;
    }

    private void OnNavVpnTapped(object sender, TappedEventArgs e) => NavTapped?.Invoke(this, "vpn");
    private void OnNavProfileTapped(object sender, TappedEventArgs e) => NavTapped?.Invoke(this, "profile");
    private void OnNavDevicesTapped(object sender, TappedEventArgs e) => NavTapped?.Invoke(this, "devices");
    private void OnNavPasswordTapped(object sender, TappedEventArgs e) => NavTapped?.Invoke(this, "password");
    private void OnNavSplitTapped(object sender, TappedEventArgs e) => NavTapped?.Invoke(this, "split");
}
