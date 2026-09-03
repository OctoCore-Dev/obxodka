using obxodka.Client.Platforms;
using obxodka.Maui.Services;
#if WINDOWS
using Microsoft.UI;
using Microsoft.UI.Windowing;
using WinRT.Interop;
using obxodka.Platforms.Windows;
using obxodka.Maui.Platforms.Windows.Services;
#elif ANDROID
using obxodka.Maui.Platforms.Android.Services;
#endif

namespace obxodka;

internal static partial class MauiProgram
{
#if WINDOWS
    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hwnd);
#endif

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseFluentMauiIcons()
            .UseMauiCommunityToolkit()
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

        builder.ConfigureLifecycleEvents(events =>
        {
#if WINDOWS
            events.AddWindows(windows => windows.OnWindowCreated(window =>
            {
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                {
                    ConfigureWindowsWindow(window);
                }
            }));
#endif
        });

        builder.Services.AddHttpClient<ApiService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestVersion = new Version(1, 1);
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
            },
            UseProxy = false
        });

        builder.Services.AddSingleton<AuthManager>();

#if WINDOWS
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            PlatformServices.Init(
                preferences: new MauiPreferencesService(),
                secureStorage: new WindowsSecureStorageService(),
                connectivity: new WindowsConnectivityService(),
                mainThread: new MauiMainThreadService(),
                deviceInfo: new WindowsDeviceInfoService(),
                certificateAudit: new WindowsCertificateAuditService());

            builder.Services.AddSingleton<IVpnService, WindowsVpnService>();
            builder.Services.AddSingleton<IAppManager, AppManager>();
            builder.Services.AddSingleton<IAppUpdaterService, WindowsAppUpdaterService>();
        }
#elif ANDROID
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            var ctx = Android.App.Application.Context;
            PlatformServices.Init(
                preferences: new MauiPreferencesService(),
                secureStorage: new AndroidSecureStorageService(),
                connectivity: new AndroidConnectivityService(ctx),
                mainThread: new MauiMainThreadService(),
                deviceInfo: new AndroidDeviceInfoService(ctx),
                certificateAudit: new AndroidCertificateAuditService(ctx));

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
                Debug.WriteLine($"[MAUI STARTUP ERROR] {ex}");
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Obxodka");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "startup_error.log"), $"[{DateTime.UtcNow:O}] {ex}\r\n\r\n");
            }
            catch { }

            throw;
        }
    }

    private static void ConfigurePlatformHandlers()
    {
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("Borderless", (handler, _) =>
        {
#if WINDOWS
            handler.PlatformView.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
            handler.PlatformView.Resources["TextControlBackground"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            handler.PlatformView.Resources["TextControlBackgroundPointerOver"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            handler.PlatformView.Resources["TextControlBackgroundFocused"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            handler.PlatformView.Resources["TextControlBorderBrushPointerOver"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            handler.PlatformView.Resources["TextControlBorderBrushFocused"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
#elif ANDROID
            handler.PlatformView.SetBackgroundColor(Android.Graphics.Color.Transparent);
            handler.PlatformView.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
#endif
        });

#if WINDOWS
        Microsoft.Maui.Handlers.ScrollViewHandler.Mapper.AppendToMapping("FixHorizontalOverflow", (handler, view) =>
        {
            if (view.Orientation == ScrollOrientation.Vertical)
            {
                handler.PlatformView.HorizontalScrollMode = Microsoft.UI.Xaml.Controls.ScrollMode.Disabled;
                handler.PlatformView.HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled;
            }
        });
#endif
    }

#if ANDROID
    [SupportedOSPlatform("android29.0")]
    private static void RegisterAndroidServices(IServiceCollection services)
    {
        _ = services.AddSingleton<IVpnService>(_ => Platforms.Android.AndroidVpnService.Instance);
        _ = services.AddSingleton<IAppManager, Platforms.Android.AppManager>();
        _ = services.AddSingleton<IAppUpdaterService, AndroidAppUpdaterService>();
    }
#endif

#if WINDOWS
    [SupportedOSPlatform("windows10.0.19041.0")]
    private static void ConfigureWindowsWindow(Microsoft.UI.Xaml.Window window)
    {
        window.ExtendsContentIntoTitleBar = true;

        var handle = WindowNative.GetWindowHandle(window);
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
            titleBar.BackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.InactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        }

        var dpi = GetDpiForWindow(handle);
        var scale = dpi / 96.0;

        var physWidth = (int)(1150 * scale);
        var physHeight = (int)(750 * scale);

        var displayArea = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary);
        physWidth = Math.Min(physWidth, displayArea.WorkArea.Width);
        physHeight = Math.Min(physHeight, displayArea.WorkArea.Height);

        appWindow.Resize(new Windows.Graphics.SizeInt32(physWidth, physHeight));
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = true;
        }

        appWindow.Changed += (sender, _) =>
        {
            var currentDpi = GetDpiForWindow(handle);
            var currentScale = currentDpi / 96.0;
            var expectedWidth = (int)(1150 * currentScale);
            var expectedHeight = (int)(750 * currentScale);

            var currentDisplay = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary);
            expectedWidth = Math.Min(expectedWidth, currentDisplay.WorkArea.Width);
            expectedHeight = Math.Min(expectedHeight, currentDisplay.WorkArea.Height);

            if (sender.Size.Width != expectedWidth || sender.Size.Height != expectedHeight)
            {
                sender.Resize(new Windows.Graphics.SizeInt32(expectedWidth, expectedHeight));
            }
        };
    }
#endif
}
