using Microsoft.Maui.Handlers;
#if WINDOWS
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using WinRT.Interop;
using Colors = Microsoft.UI.Colors;
using SolidColorBrush = Microsoft.UI.Xaml.Media.SolidColorBrush;
using Thickness = Microsoft.UI.Xaml.Thickness;
using WinUiWindow = Microsoft.UI.Xaml.Window;
using ScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility;
using ScrollMode = Microsoft.UI.Xaml.Controls.ScrollMode;

#elif ANDROID
using Android.Content.Res;
using Google.Android.Material.MaterialSwitch;
using Application = Android.App.Application;
using Color = Android.Graphics.Color;
#endif


namespace obxodka;

internal static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseFluentMauiIcons()
            .UseMauiCommunityToolkit()
            .UseMauiCommunityToolkitMediaElement(false)
            .UseSkiaSharp()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                fonts.AddFont("Commissioner-ExtraBold.ttf", "CommissionerExtraBold");
                fonts.AddFont("Roboto-Regular.ttf", "RobotoRegular");
                fonts.AddFont("Roboto-Medium.ttf", "RobotoMedium");
                fonts.AddFont("Roboto-Bold.ttf", "RobotoBold");
            });

        ConfigurePlatformHandlers();

#if WINDOWS
        builder.ConfigureLifecycleEvents(events => events.AddWindows(windows => windows.OnWindowCreated(window =>
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                ConfigureWindowsWindow(window);
            }
        })));
#endif

        builder.Services.AddHttpClient<ApiService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestVersion = new Version(1, 1);
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }).ConfigurePrimaryHttpMessageHandler(ApiService.CreateDefaultHandler);

        builder.Services.AddSingleton<AuthManager>();
        builder.Services.AddSingleton<ThemeManager>();
        builder.Services.AddSingleton<IPreferencesService, MauiPreferencesService>();
        builder.Services.AddSingleton<IMainThreadService, MauiMainThreadService>();

#if WINDOWS
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            PlatformServices.Init(
                new MauiPreferencesService(),
                new WindowsSecureStorageService(),
                new WindowsConnectivityService(),
                new MauiMainThreadService(),
                new WindowsDeviceInfoService(),
                new WindowsCertificateAuditService(),
                new WindowsNotificationService());

            WindowsVpnService.SyncSplitTunnelPolicy = AppManager.SyncPolicyFromPreferences;
            builder.Services.AddSingleton<ITunnelPlatformAdapter, WindowsVpnService>();
            builder.Services.AddSingleton<IVpnService>(sp => new VpnSessionOrchestrator(sp.GetRequiredService<ITunnelPlatformAdapter>()));
            builder.Services.AddSingleton<IAppManager, AppManager>();
            builder.Services.AddSingleton<IAppUpdaterService, WindowsAppUpdaterService>();
        }
#elif ANDROID
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var ctx = Application.Context;
            PlatformServices.Init(
                new MauiPreferencesService(),
                new AndroidSecureStorageService(),
                new AndroidConnectivityService(ctx),
                new MauiMainThreadService(),
                new AndroidDeviceInfoService(ctx),
                new AndroidCertificateAuditService(ctx));

            RegisterAndroidServices(builder.Services);
        }
#endif

        builder.Services.AddTransient<MainPage>();

        try
        {
            return builder.Build();
        }
        catch (Exception ex)
        {
            try
            {
                AppLogger.LogError("[MAUI STARTUP ERROR]", ex);
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Obxodka");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "startup_error.log"), $"[{DateTime.UtcNow:O}] {ex}\r\n\r\n");
            }
            catch
            {
                // ignored
            }

            throw;
        }
    }

    private static void ConfigurePlatformHandlers()
    {
        EntryHandler.Mapper.AppendToMapping("Borderless", (handler, _) =>
        {
#if WINDOWS
            handler.PlatformView.BorderThickness = new Thickness(0);
            handler.PlatformView.Resources["TextControlBackground"] =
                new SolidColorBrush(Colors.Transparent);
            handler.PlatformView.Resources["TextControlBackgroundPointerOver"] =
                new SolidColorBrush(Colors.Transparent);
            handler.PlatformView.Resources["TextControlBackgroundFocused"] =
                new SolidColorBrush(Colors.Transparent);
            handler.PlatformView.Resources["TextControlBorderBrushPointerOver"] =
                new SolidColorBrush(Colors.Transparent);
            handler.PlatformView.Resources["TextControlBorderBrushFocused"] =
                new SolidColorBrush(Colors.Transparent);
#elif ANDROID
            handler.PlatformView.SetBackgroundColor(Color.Transparent);
            handler.PlatformView.BackgroundTintList = ColorStateList.ValueOf(Color.Transparent);
#endif
        });

        SwitchHandler.Mapper.AppendToMapping("CleanSwitch", (handler, _) =>
        {
#if WINDOWS
            handler.PlatformView.OnContent = null;
            handler.PlatformView.OffContent = null;
#elif ANDROID
            handler.PlatformView.Background = null;
            handler.PlatformView.BackgroundTintList = null;
            if (handler.PlatformView is MaterialSwitch matSwitch)
            {
                matSwitch.ThumbIconDrawable = null;
            }
#endif
        });

#if WINDOWS
        ScrollViewHandler.Mapper.AppendToMapping("FixHorizontalOverflow", (handler, view) =>
        {
            if (view.Orientation != ScrollOrientation.Vertical)
            {
                return;
            }

            handler.PlatformView.HorizontalScrollMode = ScrollMode.Disabled;
            handler.PlatformView.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        });
#endif
    }

#if ANDROID
    [SupportedOSPlatform("android30.0")]
    private static void RegisterAndroidServices(IServiceCollection services)
    {
        var orchestrator = AndroidVpnService.Orchestrator;
        _ = services.AddSingleton<IVpnService>(_ => orchestrator);
        _ = services.AddSingleton<IAppManager, AppManager>();
        _ = services.AddSingleton<IAppUpdaterService, AndroidAppUpdaterService>();
        _ = services.AddSingleton<IQrScannerService, AndroidQrScannerService>();
    }
#endif

#if WINDOWS
    [SupportedOSPlatform("windows10.0.19041.0")]
    private static void ConfigureWindowsWindow(WinUiWindow window)
    {
        window.ExtendsContentIntoTitleBar = true;

        var handle = WindowNative.GetWindowHandle(window);
        App.MainWindowHandle = handle;
        var id = Win32Interop.GetWindowIdFromWindow(handle);
        var appWindow = AppWindow.GetFromWindowId(id);
        if (appWindow is null)
        {
            return;
        }

        appWindow.Title = "obxodka";

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var titleBar = appWindow.TitleBar;
            titleBar.ExtendsContentIntoTitleBar = true;
            titleBar.BackgroundColor = Colors.Transparent;
            titleBar.InactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }

        var dpi = MauiNativeMethods.GetDpiForWindow(handle);
        var scale = dpi / 96.0;

        var physWidth = (int)(1150 * scale);
        var physHeight = (int)(750 * scale);

        var displayArea = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary);
        physWidth = Math.Min(physWidth, displayArea.WorkArea.Width);
        physHeight = Math.Min(physHeight, displayArea.WorkArea.Height);

        appWindow.Resize(new SizeInt32(physWidth, physHeight));
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = true;
        }

        appWindow.Changed += (sender, _) =>
        {
            var currentDpi = MauiNativeMethods.GetDpiForWindow(handle);
            var currentScale = currentDpi / 96.0;
            var expectedWidth = (int)(1150 * currentScale);
            var expectedHeight = (int)(750 * currentScale);

            var currentDisplay = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary);
            expectedWidth = Math.Min(expectedWidth, currentDisplay.WorkArea.Width);
            expectedHeight = Math.Min(expectedHeight, currentDisplay.WorkArea.Height);

            if (sender.Size.Width != expectedWidth || sender.Size.Height != expectedHeight)
            {
                sender.Resize(new SizeInt32(expectedWidth, expectedHeight));
            }
        };

        appWindow.Closing += (sender, args) =>
        {
            args.Cancel = true;
            try
            {
                _ = MauiNativeMethods.ShowWindow(handle, 0);
                sender.Hide();
            }
            catch
            {
                // ignored
            }

            App.InitiateGracefulBackgroundShutdown();
        };

        window.Closed += (_, _) => App.InitiateGracefulBackgroundShutdown();
    }
#endif
}

#if WINDOWS
internal static partial class MauiNativeMethods
{
    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint GetDpiForWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
#endif
