#if WINDOWS
using Microsoft.UI;
using Microsoft.UI.Windowing;
using WinRT.Interop;
using obxodka.Platforms.Windows;
#endif
namespace obxodka;

internal static class MauiProgram
{
#if WINDOWS
#pragma warning disable SYSLIB1054
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
#pragma warning restore SYSLIB1054
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
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("NoUnderline", (handler, view) =>
{
#if ANDROID
    handler.PlatformView.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
#endif
});
        builder.ConfigureLifecycleEvents(events =>
        {
#if WINDOWS
            events.AddWindows(windows => windows
                .OnWindowCreated(window =>
                {
#pragma warning disable CA1416 
                    var handle = WindowNative.GetWindowHandle(window);
                    var id = Win32Interop.GetWindowIdFromWindow(handle);
                    var appWindow = AppWindow.GetFromWindowId(id);
                    if (appWindow != null)
                    {
                        appWindow.Title = "obxodka";

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

                        appWindow.Changed += (sender, args) =>
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
#pragma warning restore CA1416
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
#pragma warning disable CA1416
        builder.Services.AddSingleton<IVpnService, WindowsVpnService>();
        builder.Services.AddSingleton<IAppManager, AppManager>();
#pragma warning restore CA1416
#elif ANDROID
#pragma warning disable CA1416
        builder.Services.AddSingleton<IVpnService>(sp => Platforms.Android.AndroidVpnService.Instance);
        builder.Services.AddSingleton<IAppManager, Platforms.Android.AppManager>();
        builder.Services.AddSingleton<IAppUpdaterService, AndroidAppUpdaterService>();
#pragma warning restore CA1416
#endif
        builder.Services.AddTransient<MainPage>();
#if DEBUG
        builder.Logging.AddDebug();
#endif
#if WINDOWS || ANDROID
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("Borderless", (handler, view) =>
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
#endif
        });
#endif
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
}
