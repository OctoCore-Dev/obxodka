namespace obxodka;

internal sealed partial class App : Application
{
    public static event Action? AppResumed;
    public static event Action? WindowActivated;
    public static event Action? WindowDeactivated;
    public static bool PendingTileAction { get; set; }
#if WINDOWS
    public static IntPtr MainWindowHandle { get; set; } = IntPtr.Zero;
    private static bool t_isConnectivityHooked;
#endif

    public App()
    {
        InitializeComponent();
        DnsAdBlocker.IsAdBlockEnabled = Preferences.Default.Get("use_adblock_dns", true);
    }

    public static void HandleTileClick()
    {
        PendingTileAction = true;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (Shell.Current is null)
            {
                return;
            }

            await Shell.Current.GoToAsync("//main");
            if (Shell.Current.CurrentPage is MainPage)
            {
                PendingTileAction = false;
            }
        });
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new AppShell())
        {
            TitleBar = null
        };
        window.Resumed += (_, _) => AppResumed?.Invoke();
        window.Activated += (_, _) => WindowActivated?.Invoke();
        window.Deactivated += (_, _) => WindowDeactivated?.Invoke();
        window.Stopped += (_, _) => WindowDeactivated?.Invoke();

#if WINDOWS
        window.HandlerChanged += (_, _) =>
        {
            if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window winUIWindow)
            {
                winUIWindow.Activated += (s, args) =>
                {
                    if (args.WindowActivationState == Microsoft.UI.Xaml.WindowActivationState.Deactivated)
                    {
                        WindowDeactivated?.Invoke();
                    }
                    else
                    {
                        WindowActivated?.Invoke();
                    }
                };
                winUIWindow.VisibilityChanged += (s, args) =>
                {
                    if (!args.Visible)
                    {
                        WindowDeactivated?.Invoke();
                    }
                    else
                    {
                        WindowActivated?.Invoke();
                    }
                };

                winUIWindow.ExtendsContentIntoTitleBar = true;
                winUIWindow.SetTitleBar(new Microsoft.UI.Xaml.Controls.Grid { Height = 0, MaxHeight = 0 });

                var handle = WinRT.Interop.WindowNative.GetWindowHandle(winUIWindow);
                MainWindowHandle = handle;
                var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
                var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
                if (appWindow is not null && Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
                {
                    var titleBar = appWindow.TitleBar;
                    titleBar.ExtendsContentIntoTitleBar = true;
                    titleBar.BackgroundColor = Microsoft.UI.Colors.Transparent;
                    titleBar.InactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                    titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                    titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

                    void SyncTitleBarColors()
                    {
                        var bgBase = Current?.Resources != null && Current.Resources.TryGetValue("BgBase", out var bObj) && bObj is Color c
                            ? c
                            : Color.FromArgb("#0E0E14");

                        var luminance = (0.2126 * bgBase.Red) + (0.7152 * bgBase.Green) + (0.0722 * bgBase.Blue);
                        var isLight = luminance > 0.55;

                        titleBar.ButtonForegroundColor = isLight ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
                        titleBar.ButtonHoverForegroundColor = isLight ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
                        titleBar.ButtonInactiveForegroundColor = isLight
                            ? global::Windows.UI.Color.FromArgb(120, 0, 0, 0)
                            : global::Windows.UI.Color.FromArgb(120, 255, 255, 255);
                        titleBar.ButtonHoverBackgroundColor = isLight
                            ? global::Windows.UI.Color.FromArgb(25, 0, 0, 0)
                            : global::Windows.UI.Color.FromArgb(30, 255, 255, 255);
                        titleBar.ButtonPressedBackgroundColor = isLight
                            ? global::Windows.UI.Color.FromArgb(50, 0, 0, 0)
                            : global::Windows.UI.Color.FromArgb(60, 255, 255, 255);
                    }

                    SyncTitleBarColors();

                    Current?.RequestedThemeChanged += (_, _) => MainThread.BeginInvokeOnMainThread(SyncTitleBarColors);
                    if (IPlatformApplication.Current?.Services?.GetService<ThemeManager>() is { } tm)
                    {
                        tm.OnThemeApplied += _ => MainThread.BeginInvokeOnMainThread(SyncTitleBarColors);
                        tm.OnThemeReset += () => MainThread.BeginInvokeOnMainThread(SyncTitleBarColors);
                    }
                }
            }
        };

        window.Created += (_, _) => _ = OctopusEngine.StartRelayIfEnabledAsync();

        if (!t_isConnectivityHooked)
        {
            t_isConnectivityHooked = true;
            Connectivity.Current.ConnectivityChanged += (_, e) => _ = e.NetworkAccess != NetworkAccess.Internet ? OctopusEngine.StopRelayAsync() : OctopusEngine.StartRelayIfEnabledAsync();
        }

        window.Destroying += (_, _) =>
        {
            try
            {
                var vpnService = IPlatformApplication.Current?.Services?.GetService<IVpnService>();
                vpnService?.StopVpnAsync().GetAwaiter().GetResult();
                OctopusEngine.StopRelayAsync().GetAwaiter().GetResult();
            }
            catch { }

            Environment.Exit(0);
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                var vpnService = IPlatformApplication.Current?.Services?.GetService<IVpnService>();
                vpnService?.StopVpnAsync().GetAwaiter().GetResult();
                OctopusEngine.StopRelayAsync().GetAwaiter().GetResult();
            }
            catch { }
        };
#endif

        return window;
    }
}
