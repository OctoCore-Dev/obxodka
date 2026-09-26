namespace obxodka.Maui.Platforms.Windows.Services;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed partial class WindowsNotificationService : INotificationService
{
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appID);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    private static global::Windows.UI.Notifications.ToastNotification? t_activeToast;
    private static long t_lastNotificationTicks;
    private static bool t_aumidConfigured;
    private static bool t_appSdkHandlerRegistered;

    public event Action? ReconnectRequested;

    public WindowsNotificationService() => RegisterAppSdkNotificationHandler();

    private void RegisterAppSdkNotificationHandler()
    {
        if (t_appSdkHandlerRegistered)
        {
            return;
        }

        try
        {
            if (global::Microsoft.Windows.AppNotifications.AppNotificationManager.IsSupported())
            {
                var manager = global::Microsoft.Windows.AppNotifications.AppNotificationManager.Default;
                manager.NotificationInvoked += (sender, args) =>
                {
                    if (App.MainWindowHandle != IntPtr.Zero)
                    {
                        try
                        {
                            _ = ShowWindow(App.MainWindowHandle, 9);
                            _ = SetForegroundWindow(App.MainWindowHandle);
                        }
                        catch { }
                    }

                    if ((args.Arguments.TryGetValue("action", out var action) && action == "reconnect") ||
                        args.Arguments.ContainsKey("reconnect"))
                    {
                        PlatformServices.MainThread.BeginInvokeOnMainThread(() => ReconnectRequested?.Invoke());
                    }
                };
                manager.Register();
                t_appSdkHandlerRegistered = true;
            }
        }
        catch { }
    }

    private static bool IsPackaged()
    {
        try
        {
            return global::Windows.ApplicationModel.Package.Current?.Id != null;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureAumid()
    {
        if (t_aumidConfigured || IsPackaged())
        {
            return;
        }

        try
        {
            _ = SetCurrentProcessExplicitAppUserModelID("com.octocore.obxodka");

            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\com.octocore.obxodka");
            key?.SetValue("DisplayName", "Obxodka", Microsoft.Win32.RegistryValueKind.String);
            key?.SetValue("ShowInSettings", 1, Microsoft.Win32.RegistryValueKind.DWord);
            if (Environment.ProcessPath is { } exePath)
            {
                key?.SetValue("IconUri", exePath, Microsoft.Win32.RegistryValueKind.String);
            }

            var programsPath = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            var shortcutPath = System.IO.Path.Combine(programsPath, "Obxodka.lnk");
            if (!System.IO.File.Exists(shortcutPath) && Environment.ProcessPath is { } targetExe)
            {
                var type = Type.GetTypeFromProgID("WScript.Shell");
                if (type != null)
                {
                    dynamic? shell = Activator.CreateInstance(type);
                    if (shell != null)
                    {
                        var link = shell.CreateShortcut(shortcutPath);
                        link.TargetPath = targetExe;
                        link.Save();
                    }
                }
            }

            t_aumidConfigured = true;
        }
        catch { }
    }

    public void ShowUnexpectedDisconnectNotification()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref t_lastNotificationTicks) < 3000)
        {
            return;
        }
        _ = Interlocked.Exchange(ref t_lastNotificationTicks, now);

        try
        {
            var xml = """
            <toast scenario="reminder" duration="long">
                <visual>
                    <binding template="ToastGeneric">
                        <text>Внезапное отключение</text>
                        <text>Связь с сервером потеряна. Желаете переподключиться?</text>
                    </binding>
                </visual>
                <actions>
                    <action content="Переподключиться" arguments="reconnect" activationType="foreground"/>
                </actions>
                <audio src="ms-winsoundevent:Notification.Default"/>
            </toast>
            """;

            if (IsPackaged())
            {
                try
                {
                    if (global::Microsoft.Windows.AppNotifications.AppNotificationManager.IsSupported())
                    {
                        var notif = new global::Microsoft.Windows.AppNotifications.AppNotification(xml);
                        global::Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Show(notif);
                        return;
                    }
                }
                catch { }

                var xmlDoc = new global::Windows.Data.Xml.Dom.XmlDocument();
                xmlDoc.LoadXml(xml);
                var toast = new global::Windows.UI.Notifications.ToastNotification(xmlDoc);
                AttachToastHandlers(toast);
                t_activeToast = toast;

                var notifier = global::Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier();
                notifier.Show(toast);
            }
            else
            {
                EnsureAumid();

                var xmlDoc = new global::Windows.Data.Xml.Dom.XmlDocument();
                xmlDoc.LoadXml(xml);
                var toast = new global::Windows.UI.Notifications.ToastNotification(xmlDoc);
                AttachToastHandlers(toast);
                t_activeToast = toast;

                global::Windows.UI.Notifications.ToastNotifier notifier;
                try
                {
                    notifier = global::Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier("com.octocore.obxodka");
                }
                catch
                {
                    notifier = global::Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier();
                }

                notifier.Show(toast);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TOAST NOTIFICATION ERROR] {ex.Message}");
        }
    }

    private void AttachToastHandlers(global::Windows.UI.Notifications.ToastNotification toast)
    {
        toast.Activated += (sender, args) =>
        {
            if (App.MainWindowHandle != IntPtr.Zero)
            {
                try
                {
                    _ = ShowWindow(App.MainWindowHandle, 9);
                    _ = SetForegroundWindow(App.MainWindowHandle);
                }
                catch { }
            }

            if (args is global::Windows.UI.Notifications.ToastActivatedEventArgs toastArgs &&
                toastArgs.Arguments == "reconnect")
            {
                PlatformServices.MainThread.BeginInvokeOnMainThread(() => ReconnectRequested?.Invoke());
            }
        };
    }
}
