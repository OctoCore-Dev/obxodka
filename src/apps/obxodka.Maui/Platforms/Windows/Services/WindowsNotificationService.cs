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

    public event Action? ReconnectRequested;

    private static void EnsureAumid()
    {
        if (t_aumidConfigured)
        {
            return;
        }

        try
        {
            _ = SetCurrentProcessExplicitAppUserModelID("com.octocore.obxodka");
            t_aumidConfigured = true;
        }
        catch { }
    }

    public void ShowUnexpectedDisconnectNotification()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref t_lastNotificationTicks) < 5000)
        {
            return;
        }
        _ = Interlocked.Exchange(ref t_lastNotificationTicks, now);

        try
        {
            EnsureAumid();

            var xml = """
            <toast duration="long">
                <visual>
                    <binding template="ToastGeneric">
                        <text>Внезапное отключение</text>
                        <text>Желаете переподключиться?</text>
                    </binding>
                </visual>
                <actions>
                    <action content="Переподключиться" arguments="reconnect" activationType="foreground"/>
                </actions>
            </toast>
            """;

            var xmlDoc = new global::Windows.Data.Xml.Dom.XmlDocument();
            xmlDoc.LoadXml(xml);

            var toast = new global::Windows.UI.Notifications.ToastNotification(xmlDoc);
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
        catch (Exception ex)
        {
            Debug.WriteLine($"[TOAST NOTIFICATION ERROR] {ex.Message}");
        }
    }
}
