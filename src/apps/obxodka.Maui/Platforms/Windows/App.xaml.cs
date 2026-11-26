using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace obxodka.WinUI;

//точка входа winui 3 приложения с поддержкой single instance и маршрутизацией активации окна
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed partial class App : MauiWinUIApplication
{
    public App()
    {
        //регистрация единственного экземпляра приложения для исключения сетевых конфликтов
        var mainInstance = AppInstance.FindOrRegisterForKey("obxodka_main_instance");
        if (!mainInstance.IsCurrent)
        {
            try
            {
                var args = AppInstance.GetCurrent().GetActivatedEventArgs();
                _ = mainInstance.RedirectActivationToAsync(args).AsTask().ContinueWith(
                    static _ => Process.GetCurrentProcess().Kill(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch
            {
                Process.GetCurrentProcess().Kill();
            }
            return;
        }

        mainInstance.Activated += OnAppActivated;

        InitializeComponent();
        UnhandledException += App_UnhandledException;
    }

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    //восстановление и подъем окна на передний план при повторном запуске или клике на уведомление
    private static void OnAppActivated(object? sender, AppActivationArguments args)
    {
        if (obxodka.App.MainWindowHandle != IntPtr.Zero)
        {
            try
            {
                _ = ShowWindow(obxodka.App.MainWindowHandle, 9);
                _ = SetForegroundWindow(obxodka.App.MainWindowHandle);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"[WINUI] Ошибка вывода окна на передний план: {ex.Message}");
            }
        }

        HandleIncomingActivation(args);
    }

    private static void HandleIncomingActivation(AppActivationArguments? args)
    {
        if (args is null)
        {
            return;
        }

        try
        {
            if (args.Kind == ExtendedActivationKind.ToastNotification &&
                args.Data is Windows.ApplicationModel.Activation.ToastNotificationActivatedEventArgs toastArgs &&
                toastArgs.Argument.Contains("reconnect", StringComparison.Ordinal))
            {
                PlatformServices.Notification.RequestReconnect();
            }
            else if (args.Kind == ExtendedActivationKind.Launch &&
                     args.Data is Windows.ApplicationModel.Activation.LaunchActivatedEventArgs launchArgs &&
                     (launchArgs.Arguments.Contains("reconnect", StringComparison.Ordinal) ||
                      launchArgs.Arguments.Contains("ToastActivated", StringComparison.Ordinal)))
            {
                PlatformServices.Notification.RequestReconnect();
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[WINUI] Ошибка обработки активации: {ex.Message}");
        }
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);
        try
        {
            var activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            HandleIncomingActivation(activatedArgs);
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[WINUI] Ошибка чтения аргументов запуска: {ex.Message}");
        }
    }

    //глобальный перехват необработанных исключений пользовательского интерфейса
    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            AppLogger.LogError($"[WINUI UNHANDLED] {e.Exception.Message}", e.Exception);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WINUI UNHANDLED FALLBACK] {ex.Message}");
        }
    }
}
