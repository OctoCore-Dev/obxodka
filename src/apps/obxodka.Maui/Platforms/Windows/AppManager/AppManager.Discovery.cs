using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace obxodka.Platforms.Windows;

//обнаружение и сбор установленных приложений windows
public sealed partial class AppManager
{
    //асинхронное получение списка всех установленных приложений
    public Task<List<AppInfoItem>> GetInstalledAppsAsync() =>
        Task.Run<List<AppInfoItem>>(() =>
        {
            var bypassed = GetBypassedPackages().ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (_cachedApps is { Count: > 0 })
            {
                _ = _cachedApps.RemoveAll(a => IsSelfOrProtectedApp(a.PackageName, a.PackageName, a.Name));
                foreach (var app in _cachedApps)
                {
                    app.IsBypassed = bypassed.Contains(app.PackageName);
                }
                return [.. _cachedApps];
            }

            var discovered = new Dictionary<string, (string FriendlyName, string ExePath)>(StringComparer.OrdinalIgnoreCase);

            foreach (var customPath in GetCustomExePaths())
            {
                TryRegisterCandidate(discovered, customPath, null);
            }

            DiscoverFromRunningProcesses(discovered);
            DiscoverFromStartMenuShortcuts(discovered);
            if (OperatingSystem.IsWindows())
            {
                DiscoverFromRegistryUninstall(discovered);
            }
            DiscoverFromKnownPaths(discovered);

            var iconsDir = Path.Combine(FileSystem.Current.CacheDirectory, "AppIcons");
            _ = Directory.CreateDirectory(iconsDir);

            var apps = new List<AppInfoItem>(discovered.Count);
            var pendingIcons = new List<(AppInfoItem Item, string ExePath)>();

            foreach (var kvp in discovered)
            {
                var exeName = kvp.Key;
                var (friendlyName, exePath) = kvp.Value;
                if (IsSelfOrProtectedApp(exePath, exeName, friendlyName))
                {
                    continue;
                }
                var cachedIconPath = GetCachedIconPath(exePath, iconsDir);
                var hasCachedIcon = File.Exists(cachedIconPath);

                var item = new AppInfoItem(
                    friendlyName,
                    exeName,
                    bypassed.Contains(exeName) || bypassed.Contains(exePath),
                    hasCachedIcon ? cachedIconPath : null);

                if (!hasCachedIcon)
                {
                    pendingIcons.Add((item, exePath));
                }

                apps.Add(item);
            }

            _cachedApps = [.. apps.OrderByDescending(a => a.IsBypassed).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];

            //фоновая асинхронная подгрузка иконок без зависания ui
            if (pendingIcons.Count > 0)
            {
                _ = Task.Run(() =>
                {
                    foreach (var (item, exePath) in pendingIcons)
                    {
                        try
                        {
                            var extracted = GetOrExtractIcon(exePath, iconsDir);
                            if (!string.IsNullOrEmpty(extracted))
                            {
                                MainThread.BeginInvokeOnMainThread(() => item.IconPath = extracted);
                            }
                        }
                        catch { }
                    }
                });
            }

            return [.. _cachedApps];
        });

    //проверка и регистрация кандидата приложения
    private static void TryRegisterCandidate(
        Dictionary<string, (string FriendlyName, string ExePath)> discovered,
        string? rawExePath,
        string? suggestedName)
    {
        if (string.IsNullOrWhiteSpace(rawExePath))
        {
            return;
        }

        var cleanPath = CleanExecutablePath(rawExePath);
        if (string.IsNullOrWhiteSpace(cleanPath) || !cleanPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(winDir) && cleanPath.StartsWith(winDir, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var exeName = Path.GetFileName(cleanPath);
        if (string.IsNullOrWhiteSpace(exeName) || s_ignoredExeNames.Contains(exeName) || IsSelfOrProtectedApp(cleanPath, exeName, suggestedName))
        {
            return;
        }

        if (exeName.StartsWith("unins", StringComparison.OrdinalIgnoreCase) ||
            exeName.Contains("uninstall", StringComparison.OrdinalIgnoreCase) ||
            exeName.Contains("crash", StringComparison.OrdinalIgnoreCase) ||
            exeName.Contains("helper", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!File.Exists(cleanPath))
        {
            return;
        }

        var friendlyName = ResolveFriendlyAppName(cleanPath, suggestedName);
        if (string.IsNullOrWhiteSpace(friendlyName) || IsSelfOrProtectedApp(cleanPath, exeName, friendlyName))
        {
            return;
        }

        if (!discovered.TryGetValue(exeName, out var existing) ||
            (existing.FriendlyName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !friendlyName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            discovered[exeName] = (friendlyName, cleanPath);
        }
    }

    //очистка и нормализация пути к исполняемому файлу
    private static string CleanExecutablePath(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith('"'))
        {
            var endQuote = s.IndexOf('"', 1);
            if (endQuote > 1)
            {
                return s[1..endQuote].Trim();
            }
        }

        var commaIdx = s.LastIndexOf(',');
        if (commaIdx > 0 && s[..commaIdx].EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..commaIdx].Trim().Trim('"');
        }

        var exeIdx = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIdx > 0)
        {
            s = s[..(exeIdx + 4)].Trim().Trim('"');
        }

        return s;
    }

    //определение понятного отображаемого имени приложения
    private static string ResolveFriendlyAppName(string exePath, string? suggestedName)
    {
        if (!string.IsNullOrWhiteSpace(suggestedName))
        {
            var trimmed = suggestedName.Trim();
            if (!trimmed.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("Удалить", StringComparison.OrdinalIgnoreCase) &&
                trimmed.Length >= 2)
            {
                return trimmed;
            }
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            if (!string.IsNullOrWhiteSpace(info.FileDescription) && info.FileDescription.Trim().Length >= 2)
            {
                return info.FileDescription.Trim();
            }

            if (!string.IsNullOrWhiteSpace(info.ProductName) && info.ProductName.Trim().Length >= 2)
            {
                return info.ProductName.Trim();
            }
        }
        catch { }

        return Path.GetFileNameWithoutExtension(exePath);
    }

    //сканирование активных запущенных процессов
    private static void DiscoverFromRunningProcesses(Dictionary<string, (string FriendlyName, string ExePath)> discovered)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.Id <= 4)
                    {
                        continue;
                    }

                    var procName = proc.ProcessName;
                    if (string.Equals(procName, "system", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(procName, "registry", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(procName, "smss", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(procName, "csrss", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(procName, "wininit", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(procName, "services", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(procName, "lsass", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(procName, "svchost", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(procName, "fontdrvhost", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(procName, "dwm", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(procName, "sihost", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var path = GetProcessExecutablePath(proc.Id);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        TryRegisterCandidate(discovered, path, null);
                    }
                }
                catch { }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch { }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint OpenProcess(uint processAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(nint hProcess, int flags, [Out] char[] lpExeName, ref int lpdwSize);

    //получение полного пути к процессу через limited query без чтения памяти и без исключений
    private static string? GetProcessExecutablePath(int processId)
    {
        //0x1000 = PROCESS_QUERY_LIMITED_INFORMATION
        var handle = OpenProcess(0x1000, false, processId);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            var buffer = new char[1024];
            var size = buffer.Length;
            if (QueryFullProcessImageName(handle, 0, buffer, ref size) && size > 0)
            {
                return new string(buffer, 0, size);
            }
        }
        catch { }
        finally
        {
            _ = CloseHandle(handle);
        }

        return null;
    }

    //сканирование ярлыков в меню пуск и на рабочем столе
    private static void DiscoverFromStartMenuShortcuts(Dictionary<string, (string FriendlyName, string ExePath)> discovered)
    {
        string[] folders =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        ];

        foreach (var root in folders)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            try
            {
                var enumOptions = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };

                foreach (var lnkFile in Directory.EnumerateFiles(root, "*.lnk", enumOptions))
                {
                    var shortcutName = Path.GetFileNameWithoutExtension(lnkFile);
                    if (shortcutName.Contains("Uninstall", StringComparison.OrdinalIgnoreCase) ||
                        shortcutName.Contains("Удалить", StringComparison.OrdinalIgnoreCase) ||
                        shortcutName.Contains("Readme", StringComparison.OrdinalIgnoreCase) ||
                        shortcutName.Contains("Help", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (TryExtractExeFromLnk(lnkFile, out var targetExe))
                    {
                        TryRegisterCandidate(discovered, targetExe, shortcutName);
                    }
                }
            }
            catch { }
        }
    }

    //сканирование установленных программ через реестр windows uninstall
    [SupportedOSPlatform("windows")]
    private static void DiscoverFromRegistryUninstall(Dictionary<string, (string FriendlyName, string ExePath)> discovered)
    {
        (RegistryKey Root, string SubKey)[] hives =
        [
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall")
        ];

        foreach (var (root, subKeyPath) in hives)
        {
            try
            {
                using var baseKey = root.OpenSubKey(subKeyPath);
                if (baseKey is null)
                {
                    continue;
                }

                foreach (var subName in baseKey.GetSubKeyNames())
                {
                    try
                    {
                        using var appKey = baseKey.OpenSubKey(subName);
                        if (appKey is null)
                        {
                            continue;
                        }

                        var displayName = appKey.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(displayName))
                        {
                            continue;
                        }

                        var displayIcon = appKey.GetValue("DisplayIcon") as string;
                        if (!string.IsNullOrWhiteSpace(displayIcon))
                        {
                            TryRegisterCandidate(discovered, displayIcon, displayName);
                        }

                        var installLoc = appKey.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(installLoc) && Directory.Exists(installLoc))
                        {
                            var topExes = Directory.GetFiles(installLoc, "*.exe", SearchOption.TopDirectoryOnly);
                            if (topExes.Length > 0)
                            {
                                var bestExe = topExes
                                    .Where(f => !s_ignoredExeNames.Contains(Path.GetFileName(f)))
                                    .OrderByDescending(f =>
                                    {
                                        try
                                        {
                                            return new FileInfo(f).Length;
                                        }
                                        catch
                                        {
                                            return 0L;
                                        }
                                    })
                                    .FirstOrDefault();

                                if (!string.IsNullOrEmpty(bestExe))
                                {
                                    TryRegisterCandidate(discovered, bestExe, displayName);
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    //поиск известных популярных приложений по стандартным путям
    private static void DiscoverFromKnownPaths(Dictionary<string, (string FriendlyName, string ExePath)> discovered)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        (string Name, string Path)[] knownApps =
        [
            ("Telegram Desktop", Path.Combine(appData, "Telegram Desktop", "Telegram.exe")),
            ("Yandex Browser", Path.Combine(localAppData, "Yandex", "YandexBrowser", "Application", "browser.exe")),
            ("Google Chrome", Path.Combine(progFiles, "Google", "Chrome", "Application", "chrome.exe")),
            ("Microsoft Edge", Path.Combine(progFilesX86, "Microsoft", "Edge", "Application", "msedge.exe")),
            ("Mozilla Firefox", Path.Combine(progFiles, "Mozilla Firefox", "firefox.exe")),
            ("Brave Browser", Path.Combine(progFiles, "BraveSoftware", "Brave-Browser", "Application", "brave.exe")),
            ("Opera Browser", Path.Combine(localAppData, "Programs", "Opera", "opera.exe")),
            ("Opera GX", Path.Combine(localAppData, "Programs", "Opera GX", "opera.exe")),
            ("Steam", Path.Combine(progFilesX86, "Steam", "steam.exe")),
            ("Spotify", Path.Combine(appData, "Spotify", "Spotify.exe")),
            ("qBittorrent", Path.Combine(progFiles, "qBittorrent", "qbittorrent.exe")),
            ("OBS Studio", Path.Combine(progFiles, "obs-studio", "bin", "64bit", "obs64.exe")),
            ("AnyDesk", Path.Combine(progFilesX86, "AnyDesk", "AnyDesk.exe")),
            ("RustDesk", Path.Combine(progFiles, "RustDesk", "rustdesk.exe"))
        ];

        foreach (var (name, path) in knownApps)
        {
            TryRegisterCandidate(discovered, path, name);
        }

        var discordDir = Path.Combine(localAppData, "Discord");
        if (Directory.Exists(discordDir))
        {
            try
            {
                foreach (var appDir in Directory.GetDirectories(discordDir, "app-*"))
                {
                    TryRegisterCandidate(discovered, Path.Combine(appDir, "Discord.exe"), "Discord");
                }
            }
            catch { }
        }

        var robloxDir = Path.Combine(localAppData, "Roblox", "Versions");
        if (Directory.Exists(robloxDir))
        {
            try
            {
                foreach (var verDir in Directory.GetDirectories(robloxDir, "version-*"))
                {
                    TryRegisterCandidate(discovered, Path.Combine(verDir, "RobloxPlayerBeta.exe"), "Roblox Player");
                }
            }
            catch { }
        }

        var robloxX86 = Path.Combine(progFilesX86, "Roblox", "Versions");
        if (Directory.Exists(robloxX86))
        {
            try
            {
                foreach (var verDir in Directory.GetDirectories(robloxX86, "version-*"))
                {
                    TryRegisterCandidate(discovered, Path.Combine(verDir, "RobloxPlayerBeta.exe"), "Roblox Player");
                }
            }
            catch { }
        }
    }
}
