using Microsoft.Win32;
using Windows.ApplicationModel;

namespace obxodka.Platforms.Windows;

//управление автозапуском приложения в windows (msix startup task и реестр)
internal static partial class WindowsStartupManager
{
    private const string StartupTaskId = "ObxodkaStartup";
    private const string LegacyTaskName = "ObxodkaVpnStartup";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Obxodka";

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetCurrentPackageFullName(ref int packageFullNameLength, IntPtr packageFullName);

    //проверка фактического статуса автозагрузки в windows
    public static async Task<bool> CheckEnabledAsync()
    {
        try
        {
            var isEnabled = false;
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) && HasPackageIdentity())
            {
                var startupTask = await StartupTask.GetAsync(StartupTaskId);
                isEnabled = startupTask.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            }
            else if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                isEnabled = key?.GetValue(RunValueName) is not null;
            }

            Preferences.Set(AppPreferenceKeys.RunOnStartup, isEnabled);
            return isEnabled;
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[STARTUP CHECK ERROR] {ex.Message}", ex);
            return Preferences.Get(AppPreferenceKeys.RunOnStartup, false);
        }
    }

    //установка или удаление задачи автозагрузки в windows
    public static async Task<bool> SetEnabledAsync(bool enable)
    {
        try
        {
            RemoveLegacyScheduledTask();

            var isEnabled = Preferences.Get(AppPreferenceKeys.RunOnStartup, false);
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) && HasPackageIdentity())
            {
                var startupTask = await StartupTask.GetAsync(StartupTaskId);
                if (enable)
                {
                    if (startupTask.State == StartupTaskState.DisabledByUser)
                    {
                        _ = await Launcher.OpenAsync(new Uri("ms-settings:startupapps"));
                        await Task.Delay(1000);
                        var recheck = await StartupTask.GetAsync(StartupTaskId);
                        isEnabled = recheck.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
                    }
                    else
                    {
                        var state = await startupTask.RequestEnableAsync();
                        isEnabled = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
                    }
                }
                else
                {
                    if (startupTask.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy)
                    {
                        startupTask.Disable();
                    }

                    isEnabled = false;
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
                if (key is not null)
                {
                    if (enable)
                    {
                        var exePath = Environment.ProcessPath;
                        if (!string.IsNullOrEmpty(exePath))
                        {
                            key.SetValue(RunValueName, $"\"{exePath}\"");
                            isEnabled = true;
                        }
                    }
                    else
                    {
                        key.DeleteValue(RunValueName, false);
                        isEnabled = false;
                    }
                }
            }

            Preferences.Set(AppPreferenceKeys.RunOnStartup, isEnabled);
            return isEnabled;
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[STARTUP ERROR] {ex.Message}", ex);
            return Preferences.Get(AppPreferenceKeys.RunOnStartup, false);
        }
    }

    //проверка наличия msix пакета приложения
    private static bool HasPackageIdentity()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return false;
        }

        var length = 0;
        return GetCurrentPackageFullName(ref length, IntPtr.Zero) != 15700;
    }

    //удаление устаревшей задачи планировщика при ее наличии
    private static void RemoveLegacyScheduledTask() =>
        _ = Task.Run(() =>
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    return;
                }

                using var proc = Process.Start(new ProcessStartInfo("schtasks", $"/delete /tn \"{LegacyTaskName}\" /f")
                {
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                proc?.WaitForExit();
            }
            catch
            {
                //игнорируем если задача не была ранее зарегистрирована в планировщике
            }
        });
}
