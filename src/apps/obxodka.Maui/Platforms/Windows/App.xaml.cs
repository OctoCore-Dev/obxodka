using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using obxodka.Client.Platforms;
using obxodka.Maui.Platforms.Windows.Services;

namespace obxodka.WinUI;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed partial class App : MauiWinUIApplication
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    public App()
    {
        WindowsNotificationService.EnsureAppSdkRegistered();

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
            if (args.Kind == ExtendedActivationKind.AppNotification &&
                args.Data is Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs notifArgs)
            {
                if ((notifArgs.Arguments.TryGetValue("action", out var action) && action == "reconnect") ||
                    notifArgs.Arguments.ContainsKey("reconnect"))
                {
                    PlatformServices.Notification.RequestReconnect();
                }
            }
            else if (args.Kind == ExtendedActivationKind.ToastNotification &&
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
                if (launchArgs.Arguments.Contains("reconnect"))
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

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Obxodka",
                "unhandled.log");

            _ = Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var logEntry = $"[{DateTime.UtcNow:O}] {e.Exception}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(logPath, logEntry);
            e.Handled = true;
        }
        catch { }
    }
}
