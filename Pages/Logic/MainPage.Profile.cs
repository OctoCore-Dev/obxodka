namespace obxodka.Pages;

internal sealed partial class MainPage
{
    private async void LoadProfileTabDataAsync(UserSession? session)
    {
        var s = session ?? await AuthManager.LoadSessionAsync();
        ProfileEmailLabel.Text = !string.IsNullOrEmpty(s.Email) ? s.Email : "Гость";
        ProfileAdBlockSwitch.IsToggled = Preferences.Default.Get("use_adblock_dns", false);
    }

    private async void OnLogoutClickedAsync(object? sender, EventArgs e)
    {
        if (sender is VisualElement btn)
        {
            await btn.BounceClickAsync();
        }

        await _vpnService.StopVpnAsync();
        await AuthManager.RemoveCurrentDeviceFromServerAsync();
        await AuthManager.ClearSessionAsync();

        DesktopSidebar.IsVisible = false;
        MobileBottomBar.IsVisible = false;
        SwitchTab("auth");
    }

    private void OnAdBlockToggled(object? sender, ToggledEventArgs e) =>
        Preferences.Default.Set("use_adblock_dns", e.Value);
}
