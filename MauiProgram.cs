namespace obxodka;
internal static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                fonts.AddFont("Commissioner-ExtraBold.ttf", "CommissionerExtraBold");
            });
        builder.ConfigureLifecycleEvents(events => {
#if WINDOWS
    events.AddWindows(windows => windows
        .OnWindowCreated(window => { 
            var handle = WindowNative.GetWindowHandle(window);
            var id = Win32Interop.GetWindowIdFromWindow(handle);
            var appWindow = AppWindow.GetFromWindowId(id);
            if (appWindow != null) {
                appWindow.Title = "obxodka"; 
                appWindow.Resize(new Windows.Graphics.SizeInt32(500, 700)); 
                if (appWindow.Presenter is OverlappedPresenter presenter) {
                    presenter.IsResizable = false; 
                    presenter.IsMaximizable = false; 
                }
            }
        }));
#endif
        });
        builder.Services.AddSingleton(sp =>
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
                {
                    if (cert == null) return false;
                    var pk = cert.GetPublicKey();
                    using var sha256 = System.Security.Cryptography.SHA256.Create();
                    var hash = sha256.ComputeHash(pk);
                    var base64Hash = Convert.ToBase64String(hash);
                    return base64Hash == AppSecrets.SslPublicKeyHash;
                }
            };
            return new HttpClient(handler)
            {
                BaseAddress = new Uri(AppConfig.ApiBaseUrl),
                Timeout = TimeSpan.FromSeconds(20)
            };
        });
        builder.Services.AddSingleton<Core.ApiService>();
        builder.Services.AddSingleton<Core.AuthManager>();
        builder.Services.AddSingleton<Core.AuthManager>();
        builder.Services.AddSingleton<Core.ApiService>();
        builder.Services.AddSingleton<Core.AuthManager>();
#if WINDOWS
        builder.Services.AddSingleton<obxodka.Core.IVpnService, obxodka.Platforms.Windows.WindowsVpnService>();
#elif ANDROID
        builder.Services.AddSingleton<obxodka.Core.IVpnService, AndroidVpnService>();
#endif
        builder.Services.AddSingleton<Pages.SplashPage>();
        builder.Services.AddSingleton<Pages.MainPage>();
        builder.Services.AddTransient<Pages.LoginPage>();
        builder.Services.AddTransient<Pages.RegisterPage>();
        builder.Services.AddTransient<Pages.UserProfilePage>();
        builder.Services.AddTransient<Pages.DevicesPage>();
        builder.Services.AddTransient<Pages.ChangePasswordPage>();
        builder.Services.AddTransient<Pages.DeleteAccountPage>();
        builder.Services.AddTransient<Pages.PaymentPage>();
#if DEBUG
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}