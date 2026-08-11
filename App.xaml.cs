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
                winUIWindow.ExtendsContentIntoTitleBar = true;

                var handle = WinRT.Interop.WindowNative.GetWindowHandle(winUIWindow);
                var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
                var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
                if (appWindow != null && Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
                {
                    appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
                    appWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                    appWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                    appWindow.TitleBar.ButtonHoverBackgroundColor = global::Windows.UI.Color.FromArgb(30, 255, 255, 255);
                    appWindow.TitleBar.ButtonPressedBackgroundColor = global::Windows.UI.Color.FromArgb(60, 255, 255, 255);
                    appWindow.TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
                    appWindow.TitleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
                    appWindow.TitleBar.ButtonInactiveForegroundColor = global::Windows.UI.Color.FromArgb(120, 255, 255, 255);
                }

                var backdropMode = Preferences.Get("WindowsBackdropMode", "Acrylic");
                Platforms.Windows.WindowsBackdropHelper.ApplyBackdrop(winUIWindow, backdropMode);
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
