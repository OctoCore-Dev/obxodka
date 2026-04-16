namespace obxodka;
internal sealed partial class App : Microsoft.Maui.Controls.Application
{
    private readonly IServiceProvider _services;
    public static bool PendingTileAction { get; set; }
    public App(IServiceProvider services)
    {
        InitializeComponent();
        ThemeManager.LoadSavedTheme();
        _services = services;
    }
    public static void HandleTileClick()
    {
        PendingTileAction = true;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var window = Current?.Windows.FirstOrDefault();
            var mainPage = window?.Page?.Navigation?.NavigationStack?
                .FirstOrDefault(p => p is MainPage) as MainPage
                ?? window?.Page as MainPage;
            if (mainPage != null)
            {
                PendingTileAction = false;
                mainPage.ExecuteConnectClickFromTile();
            }
        });
    }
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var splashPage = _services.GetRequiredService<SplashPage>();
        var window = new Window(splashPage);
#if WINDOWS
    window.Destroying += (s, e) => {
        var vpnService = _services.GetService<IVpnService>();
        vpnService?.StopVpn(); 
    };
#endif
        return window; 
    }
}