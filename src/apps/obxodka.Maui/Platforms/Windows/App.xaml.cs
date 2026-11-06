using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace obxodka.WinUI;

//точка входа winui 3 приложения с поддержкой single instance и маршрутизацией активации окна
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed partial class App : MauiWinUIApplication
{
    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    public App()
    {
        //регистрация единственного экземпляра приложения для исключения сетевых конфликтов
        var mainInstance = AppInstance.FindOrRegisterForKey("obxodka_main_instance");
        if (!mainInstance.IsCurrent)
        {
            try
            {
                var args = AppInstance.GetCurrent().GetActivatedEventArgs();
                mainInstance.RedirectActivationToAsync(args).AsTask().Wait();
            }
            catch { }

            Process.GetCurrentProcess().Kill();
            return;
        }

        mainInstance.Activated += OnAppActivated;

        InitializeComponent();
        UnhandledException += App_UnhandledException;
    }

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
            catch { }
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
                args.Data is Windows.ApplicationModel.Activation.ToastNotificationActivatedEventArgs toastArgs)
            {
                if (toastArgs.Argument.Contains("reconnect"))
                {
                    PlatformServices.Notification.RequestReconnect();
                }
            }
            else if (args.Kind == ExtendedActivationKind.Launch &&
                    args.Data is Windows.ApplicationModel.Activation.LaunchActivatedEventArgs launchArgs)
            {
                if (launchArgs.Arguments.Contains("reconnect") || launchArgs.Arguments.Contains("ToastActivated"))
                {
                    PlatformServices.Notification.RequestReconnect();
                }
            }
        }
        catch { }
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
        catch { }
    }

    //глобальный перехват необработанных исключений пользовательского интерфейса
    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            AppLogger.LogError($"[WINUI UNHANDLED] {e.Exception.Message}", e.Exception);
            e.Handled = true;
        }
        catch { }
    }
}
