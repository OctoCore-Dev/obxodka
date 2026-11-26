using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace obxodka.Platforms.Windows;

//системная служба всплывающих уведомлений windows toast notification и активации окна
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed partial class WindowsNotificationService : INotificationService
{
    //держим ссылку на активный toast, чтобы gc не отписал обработчик activated
    // ReSharper disable once NotAccessedField.Local
    private ToastNotification? _activeToast;
    private long _lastNotificationTicks;
    private long _lastReconnectTicks;
    private static bool s_aumidConfigured;

    public event Action? ReconnectRequested;

    //запрос переподключения с выводом главного окна на передний план
    public void RequestReconnect()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastReconnectTicks) < 2000)
        {
            return;
        }

        _ = Interlocked.Exchange(ref _lastReconnectTicks, now);

        if (App.MainWindowHandle != IntPtr.Zero)
        {
            try
            {
                _ = ShowWindow(App.MainWindowHandle, 9);
                _ = SetForegroundWindow(App.MainWindowHandle);
            }
            catch
            {
                // ignored
            }
        }

        PlatformServices.MainThread.BeginInvokeOnMainThread(() => ReconnectRequested?.Invoke());
    }

    //показ интерактивного уведомления о внезапном разрыве соединения с кнопкой повтора
    public void ShowUnexpectedDisconnectNotification()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastNotificationTicks) < 3000)
        {
            return;
        }

        _ = Interlocked.Exchange(ref _lastNotificationTicks, now);

        try
        {
            const string xml = """
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

            var xmlDoc = new XmlDocument();
            xmlDoc.LoadXml(xml);
            var toast = new ToastNotification(xmlDoc);
            AttachToastHandlers(toast);
            _activeToast = toast;

            ToastNotifier notifier;
            if (IsPackaged())
            {
                notifier = ToastNotificationManager.CreateToastNotifier();
            }
            else
            {
                EnsureAumid();
                try
                {
                    notifier = ToastNotificationManager.CreateToastNotifier("com.octocore.obxodka");
                }
                catch
                {
                    notifier = ToastNotificationManager.CreateToastNotifier();
                }
            }

            notifier.Show(toast);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[TOAST NOTIFICATION ERROR] {ex.Message}");
        }
    }

    [LibraryImport("shell32.dll", EntryPoint = "SetCurrentProcessExplicitAppUserModelID", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetCurrentProcessExplicitAppUserModelId(string appId);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetCurrentPackageFullName(ref int packageFullNameLength, nint packageFullName);

    private static bool IsPackaged()
    {
        var len = 0;
        var rc = GetCurrentPackageFullName(ref len, 0);
        return rc != 15700;
    }

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CoCreateInstance(
        in Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        in Guid riid,
        out IntPtr ppv);

    //создание ярлыка в меню пуск напрямую через com vtable ishelllinkw/ipersistfile без dynamic и wscript
    private static unsafe void CreateStartMenuShortcut(string shortcutPath, string targetExe)
    {
        var clsidShellLink = new Guid("00021401-0000-0000-C000-000000000046");
        var iidShellLinkW = new Guid("000214F9-0000-0000-C000-000000000046");
        var iidPersistFile = new Guid("0000010B-0000-0000-C000-000000000046");

        if (CoCreateInstance(in clsidShellLink, IntPtr.Zero, 1, in iidShellLinkW, out var pShellLink) < 0 ||
            pShellLink == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var vtblShellLink = *(nint**)pShellLink;
            var setPath = (delegate* unmanaged[Stdcall]<IntPtr, char*, int>)vtblShellLink[20];
            fixed (char* pTarget = targetExe)
            {
                if (setPath(pShellLink, pTarget) < 0)
                {
                    return;
                }
            }

            if (Marshal.QueryInterface(pShellLink, in iidPersistFile, out var pPersistFile) < 0 ||
                pPersistFile == IntPtr.Zero)
            {
                return;
            }

            try
            {
                var vtblPersistFile = *(nint**)pPersistFile;
                var save = (delegate* unmanaged[Stdcall]<IntPtr, char*, int, int>)vtblPersistFile[6];
                fixed (char* pShortcut = shortcutPath)
                {
                    _ = save(pPersistFile, pShortcut, 1);
                }
            }
            finally
            {
                _ = Marshal.Release(pPersistFile);
            }
        }
        finally
        {
            _ = Marshal.Release(pShellLink);
        }
    }

    //регистрация aumid для корректной доставки уведомлений в не пакетированных приложениях
    private static void EnsureAumid()
    {
        if (s_aumidConfigured || IsPackaged())
        {
            return;
        }

        try
        {
            _ = SetCurrentProcessExplicitAppUserModelId("com.octocore.obxodka");

            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\com.octocore.obxodka");
            key.SetValue("DisplayName", "Obxodka", RegistryValueKind.String);
            key.SetValue("ShowInSettings", 1, RegistryValueKind.DWord);
            if (Environment.ProcessPath is { } exePath)
            {
                key.SetValue("IconUri", exePath, RegistryValueKind.String);
            }

            var programsPath = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            var shortcutPath = Path.Join(programsPath, "Obxodka.lnk");
            if (!File.Exists(shortcutPath) && Environment.ProcessPath is { } targetExe)
            {
                CreateStartMenuShortcut(shortcutPath, targetExe);
            }

            s_aumidConfigured = true;
        }
        catch
        {
            // ignored
        }
    }

    private static void AttachToastHandlers(ToastNotification toast)
    {
        toast.Activated += (_, args) =>
        {
            if (args is ToastActivatedEventArgs toastArgs &&
                (toastArgs.Arguments == "reconnect" || toastArgs.Arguments.Contains("reconnect")))
            {
                PlatformServices.Notification.RequestReconnect();
            }
        };
    }
}
