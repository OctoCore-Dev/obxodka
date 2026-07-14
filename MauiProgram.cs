#if WINDOWS
using Microsoft.UI;
using Microsoft.UI.Windowing;
using WinRT.Interop;
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
            .UseSkiaSharp()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                fonts.AddFont("Commissioner-ExtraBold.ttf", "CommissionerExtraBold");
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
                        appWindow.Resize(new Windows.Graphics.SizeInt32(1300, 750));
                        if (appWindow.Presenter is OverlappedPresenter presenter)
                        {
                            presenter.IsResizable = false;
                            presenter.IsMaximizable = false;
                            presenter.IsMinimizable = true;
                        }
                    }
#pragma warning restore CA1416
                }));
#endif
        });
        builder.Services.AddHttpClient<ApiService>(client =>
        {
            client.BaseAddress = new Uri(AppConfig.ApiBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(20);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, cert, chain, errors) => true
        });
        builder.Services.AddSingleton<AuthManager>();
#if WINDOWS
#pragma warning disable CA1416
        builder.Services.AddSingleton<IVpnService, Platforms.Windows.WindowsVpnService>();
#pragma warning restore CA1416
#elif ANDROID
#pragma warning disable CA1416
        builder.Services.AddSingleton<IVpnService>(sp => Platforms.Android.AndroidVpnService.Instance);
#pragma warning restore CA1416
#endif
        builder.Services.AddTransient<Pages.MainPage>();
#if DEBUG
        builder.Logging.AddDebug();
#endif
#if WINDOWS
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("Borderless", (handler, view) =>
        {
            handler.PlatformView.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
            handler.PlatformView.Resources["TextControlBackground"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            handler.PlatformView.Resources["TextControlBackgroundPointerOver"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            handler.PlatformView.Resources["TextControlBackgroundFocused"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            handler.PlatformView.Resources["TextControlBorderBrushPointerOver"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            handler.PlatformView.Resources["TextControlBorderBrushFocused"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
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
