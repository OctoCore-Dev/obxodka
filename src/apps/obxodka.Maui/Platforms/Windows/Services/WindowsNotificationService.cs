namespace obxodka.Platforms.Windows;

//системная служба всплывающих уведомлений windows toast notification и активации окна
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed partial class WindowsNotificationService : INotificationService
{
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appID);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    private static global::Windows.UI.Notifications.ToastNotification? s_activeToast;
    private static long s_lastNotificationTicks;
    private static long s_lastReconnectTicks;
    private static bool s_aumidConfigured;

    public event Action? ReconnectRequested;

    //запрос переподключения с выводом главного окна на передний план
    public void RequestReconnect()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref s_lastReconnectTicks) < 2000)
        {
            return;
        }
        _ = Interlocked.Exchange(ref s_lastReconnectTicks, now);

        if (App.MainWindowHandle != IntPtr.Zero)
        {
            try
            {
                _ = ShowWindow(App.MainWindowHandle, 9);
                _ = SetForegroundWindow(App.MainWindowHandle);
            }
            catch { }
        }

        PlatformServices.MainThread.BeginInvokeOnMainThread(() => ReconnectRequested?.Invoke());
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetCurrentPackageFullName(ref int packageFullNameLength, nint packageFullName);

    private static bool IsPackaged()
    {
        var len = 0;
        var rc = GetCurrentPackageFullName(ref len, 0);
        return rc != 15700;
    }

    //регистрация aumid для корректной доставки уведомлений в непакетированных приложениях
    private static void EnsureAumid()
    {
        if (s_aumidConfigured || IsPackaged())
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
            var shortcutPath = Path.Combine(programsPath, "Obxodka.lnk");
            if (!File.Exists(shortcutPath) && Environment.ProcessPath is { } targetExe)
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

            s_aumidConfigured = true;
        }
        catch { }
    }

    //показ интерактивного уведомления о внезапном разрыве соединения с кнопкой повтора
    public void ShowUnexpectedDisconnectNotification()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref s_lastNotificationTicks) < 3000)
        {
            return;
        }
        _ = Interlocked.Exchange(ref s_lastNotificationTicks, now);

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
                    <action content="Переподключиться" arguments="reconnect" activationType="background"/>
                </actions>
                <audio src="ms-winsoundevent:Notification.Default"/>
            </toast>
            """;

            var xmlDoc = new global::Windows.Data.Xml.Dom.XmlDocument();
            xmlDoc.LoadXml(xml);
            var toast = new global::Windows.UI.Notifications.ToastNotification(xmlDoc);
            AttachToastHandlers(toast);
            s_activeToast = toast;

            global::Windows.UI.Notifications.ToastNotifier notifier;
            if (IsPackaged())
            {
                notifier = global::Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier();
            }
            else
            {
                EnsureAumid();
                try
                {
                    notifier = global::Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier("com.octocore.obxodka");
                }
                catch
                {
                    notifier = global::Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier();
                }
            }

            notifier.Show(toast);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[TOAST NOTIFICATION ERROR] {ex.Message}");
        }
    }

    private static void AttachToastHandlers(global::Windows.UI.Notifications.ToastNotification toast)
    {
        toast.Activated += (sender, args) =>
        {
            if (args is global::Windows.UI.Notifications.ToastActivatedEventArgs toastArgs &&
                (toastArgs.Arguments == "reconnect" || toastArgs.Arguments.Contains("reconnect")))
            {
                PlatformServices.Notification.RequestReconnect();
            }
        };
    }
}
