using Microsoft.Win32;
using Windows.ApplicationModel;

namespace obxodka.Platforms.Windows;

//управление автозапуском приложения в windows (msix startup task и реестр)
internal static partial class WindowsStartupManager
{
    private const string StartupTaskId = "ObxodkaStartup";
    private const string ScheduledTaskName = "ObxodkaStartupTask";
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
                isEnabled = await QueryScheduledTaskExistsAsync(ScheduledTaskName).ConfigureAwait(false);
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
                //убираем устаревший ключ hkcu\run (windows игнорирует его для exe с requireAdministrator)
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
                    key?.DeleteValue(RunValueName, false);
                }
                catch
                {
                    // ignored
                }

                if (enable)
                {
                    var exePath = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        var exitCode = await RunSchtasksAsync(
                            $"/create /tn \"{ScheduledTaskName}\" /tr \"\\\"{exePath}\\\"\" /sc ONLOGON /rl HIGHEST /f").ConfigureAwait(false);
                        isEnabled = exitCode == 0;
                    }
                }
                else
                {
                    _ = await RunSchtasksAsync($"/delete /tn \"{ScheduledTaskName}\" /f").ConfigureAwait(false);
                    isEnabled = false;
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

    //проверка наличия задачи в планировщике windows
    private static async Task<bool> QueryScheduledTaskExistsAsync(string taskName) =>
        await RunSchtasksAsync($"/query /tn \"{taskName}\"").ConfigureAwait(false) == 0;

    //безопасный запуск системного schtasks.exe по полному пути из system32
    private static async Task<int> RunSchtasksAsync(string arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            return -1;
        }

        var schtasksPath = Path.Join(Environment.SystemDirectory, "schtasks.exe");
        using var proc = Process.Start(new ProcessStartInfo(schtasksPath, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        if (proc is null)
        {
            return -1;
        }

        await proc.WaitForExitAsync().ConfigureAwait(false);
        return proc.ExitCode;
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

                var schtasksPath = Path.Join(Environment.SystemDirectory, "schtasks.exe");
                using var proc = Process.Start(new ProcessStartInfo(schtasksPath, $"/delete /tn \"{LegacyTaskName}\" /f")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                proc?.WaitForExit();
            }
            catch
            {
                //игнорируем если задача не была ранее зарегистрирована в планировщике
            }
        });
}
