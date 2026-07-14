using obxodka.Pages;
namespace obxodka;

internal sealed partial class App : Application
{
    public static bool PendingTileAction { get; set; }
    public App() => InitializeComponent();
    public static void HandleTileClick()
    {
        PendingTileAction = true;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (Shell.Current == null)
            {
                return;
            }
            await Shell.Current.GoToAsync("//main");
            if (Shell.Current.CurrentPage is MainPage mainPage)
            {
                PendingTileAction = false;
            }
        });
    }
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new AppShell());
#if WINDOWS
        window.Destroying += (s, e) =>
        {
            var vpnService = IPlatformApplication.Current?.Services?.GetService<IVpnService>();
            vpnService?.StopVpnAsync();
        };
#endif
        return window;
    }
}
