namespace obxodka;

internal sealed partial class App : Application
{
    public static event Action? AppResumed;
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
        window.Resumed += (s, e) => AppResumed?.Invoke();
#if WINDOWS
        window.HandlerChanged += (s, e) =>
        {
            if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window winUIWindow)
            {
                winUIWindow.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop()
                {
                    Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt
                };
            }
        };

        window.Destroying += (s, e) =>
        {
            var vpnService = IPlatformApplication.Current?.Services?.GetService<IVpnService>();
            vpnService?.StopVpnAsync();
        };
#endif
        return window;
    }
}
