using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using SkiaSharp;

namespace obxodka.Platforms.Windows;

public sealed partial class AppManager : IAppManager
{
    private const string BypassedAppsKey = "obxodka_win_bypassed_apps";
    private const string CustomExesKey = "obxodka_win_custom_exes";
    public const string PresetGovKey = "obxodka_win_preset_gov";
    public const string PresetBanksKey = "obxodka_win_preset_banks";
    public const string PresetMediaKey = "obxodka_win_preset_media";
    public const string PresetMarketKey = "obxodka_win_preset_market";
    public const string PresetRuTldsKey = "obxodka_win_preset_rutlds";
    public const string CustomRulesKey = "obxodka_win_custom_rules";

    private static List<AppInfoItem>? t_cachedApps;

    private static readonly HashSet<string> t_ignoredExeNames = new(
    [
        "obxodka.exe",
        "obxodka.Maui.exe",
        "obxodka.Shared.exe",
        "obxodka.Client.exe",
        "createdump.exe",
        "svchost.exe",
        "conhost.exe",
        "dllhost.exe",
        "runtimebroker.exe",
        "backgroundtaskhost.exe",
        "applicationframehost.exe",
        "systemsettings.exe",
        "textinputhost.exe",
        "searchhost.exe",
        "startmenuexperiencehost.exe",
        "shellexperiencehost.exe",
        "sihost.exe",
        "taskhostw.exe",
        "ctfmon.exe",
        "dwm.exe",
        "csrss.exe",
        "smss.exe",
        "lsass.exe",
        "services.exe",
        "wininit.exe",
        "winlogon.exe",
        "fontdrvhost.exe",
        "spoolsv.exe",
        "explorer.exe",
        "msedgewebview2.exe",
        "crashpad_handler.exe",
        "unins000.exe",
        "unins001.exe",
        "uninstall.exe",
        "setup.exe",
        "update.exe",
        "updater.exe"
    ], StringComparer.OrdinalIgnoreCase);

    private static readonly string[] t_defaultBypassedExes =
    [
        "AnyDesk.exe",
        "RustDesk.exe",
        "TeamViewer.exe",
        "qbittorrent.exe",
        "uTorrent.exe",
        "Transmission-qt.exe"
    ];

    public static bool IsSelfOrProtectedApp(string? exePath, string? exeName = null, string? friendlyName = null)
    {
        if (obxodka.Core.Models.SplitTunnelPolicy.IsProtectedObxodkaEntry(exePath) ||
            obxodka.Core.Models.SplitTunnelPolicy.IsProtectedObxodkaEntry(exeName) ||
            obxodka.Core.Models.SplitTunnelPolicy.IsProtectedObxodkaEntry(friendlyName))
        {
            return true;
        }

        var selfProcessPath = Environment.ProcessPath;
        return !string.IsNullOrWhiteSpace(selfProcessPath) &&
               !string.IsNullOrWhiteSpace(exePath) &&
               string.Equals(exePath, selfProcessPath, StringComparison.OrdinalIgnoreCase);
    }

    public static void SyncPolicyFromPreferences()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return;
        }

        var policy = WindowsSplitTunnelEngine.Policy;
        policy.Enabled = true;
        policy.BypassRuGov = Preferences.Get(PresetGovKey, true);
        policy.BypassRuBanks = Preferences.Get(PresetBanksKey, true);
        policy.BypassRuMedia = Preferences.Get(PresetMediaKey, false);
        policy.BypassRuMarketplaces = Preferences.Get(PresetMarketKey, false);
        policy.BypassAllRuTlds = Preferences.Get(PresetRuTldsKey, false);

        var bypassedPackages = LoadBypassedPackagesList();
        policy.SetBypassedExecutables(bypassedPackages);

        var customRules = GetCustomRules();
        policy.SetCustomRules(customRules);

        _ = Task.Run(WindowsSplitTunnelEngine.ActivateOrReloadAsync);
    }

    public static List<string> GetCustomRules()
    {
        var raw = Preferences.Get(CustomRulesKey, string.Empty);
        return string.IsNullOrWhiteSpace(raw)
            ? []
            : [.. raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(r => !obxodka.Core.Models.SplitTunnelPolicy.IsProtectedObxodkaEntry(r))];
    }

    public static void SaveCustomRules(IEnumerable<string> rules)
    {
        var normalized = rules
            .Select(obxodka.Core.Models.SplitTunnelPolicy.NormalizeRuleEntry)
            .Where(r => !string.IsNullOrWhiteSpace(r) && !obxodka.Core.Models.SplitTunnelPolicy.IsProtectedObxodkaEntry(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Preferences.Set(CustomRulesKey, string.Join("|", normalized));
        SyncPolicyFromPreferences();
    }

    private static List<string> LoadBypassedPackagesList()
    {
        if (Preferences.ContainsKey(BypassedAppsKey))
        {
            var saved = Preferences.Get(BypassedAppsKey, string.Empty);
            return string.IsNullOrWhiteSpace(saved)
                ? []
                : [.. saved.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(p => !IsSelfOrProtectedApp(p, p))];
        }

        return [.. t_defaultBypassedExes];
    }

    public List<string> GetBypassedPackages() => LoadBypassedPackagesList();

    public void SaveBypassedPackages(List<string> packages)
    {
        var filtered = packages
            .Where(p => !string.IsNullOrWhiteSpace(p) && !IsSelfOrProtectedApp(p, p))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        Preferences.Set(BypassedAppsKey, string.Join("|", filtered));
        SyncPolicyFromPreferences();
    }

    public static List<string> GetCustomExePaths()
    {
        var saved = Preferences.Get(CustomExesKey, string.Empty);
        return string.IsNullOrWhiteSpace(saved)
            ? []
            : [.. saved.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(p => !IsSelfOrProtectedApp(p))];
    }

    public AppInfoItem? AddCustomExecutable(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var exeName = Path.GetFileName(exePath);
        if (string.IsNullOrWhiteSpace(exeName) || IsSelfOrProtectedApp(exePath, exeName))
        {
            return null;
        }

        var friendlyName = ResolveFriendlyAppName(exePath, null);
        if (IsSelfOrProtectedApp(exePath, exeName, friendlyName))
        {
            return null;
        }

        var customList = GetCustomExePaths();
        if (!customList.Contains(exePath, StringComparer.OrdinalIgnoreCase))
        {
            customList.Add(exePath);
            Preferences.Set(CustomExesKey, string.Join("|", customList));
        }

        var bypassed = GetBypassedPackages();
        if (!bypassed.Contains(exeName, StringComparer.OrdinalIgnoreCase))
        {
            bypassed.Add(exeName);
            SaveBypassedPackages(bypassed);
        }

        var iconsDir = Path.Combine(FileSystem.Current.CacheDirectory, "AppIcons");
        _ = Directory.CreateDirectory(iconsDir);
        var iconPath = GetOrExtractIcon(exePath, iconsDir);

        var existing = t_cachedApps?.FirstOrDefault(a => a.PackageName.Equals(exeName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.IsBypassed = true;
            if (!string.IsNullOrEmpty(iconPath))
            {
                existing.IconPath = iconPath;
            }
            return existing;
        }

        var item = new AppInfoItem(friendlyName, exeName, true, iconPath);
        t_cachedApps?.Insert(0, item);
        return item;
    }

    public Task<List<AppInfoItem>> GetInstalledAppsAsync() =>
        Task.Run<List<AppInfoItem>>(() =>
        {
            var bypassed = GetBypassedPackages().ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (t_cachedApps is { Count: > 0 })
            {
                _ = t_cachedApps.RemoveAll(a => IsSelfOrProtectedApp(a.PackageName, a.PackageName, a.Name));
                foreach (var app in t_cachedApps)
                {
                    app.IsBypassed = bypassed.Contains(app.PackageName);
                }
                return [.. t_cachedApps];
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

            t_cachedApps = [.. apps.OrderByDescending(a => a.IsBypassed).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];

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

            return [.. t_cachedApps];
        });

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
        if (string.IsNullOrWhiteSpace(exeName) || t_ignoredExeNames.Contains(exeName) || IsSelfOrProtectedApp(cleanPath, exeName, suggestedName))
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

                    var path = proc.MainModule?.FileName;
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
                foreach (var lnkFile in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
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

    private static bool TryExtractExeFromLnk(string lnkPath, out string exePath)
    {
        exePath = string.Empty;
        try
        {
            var bytes = File.ReadAllBytes(lnkPath);
            if (bytes.Length < 76 || bytes[0] != 0x4C)
            {
                return false;
            }

            var asciiText = Encoding.Default.GetString(bytes);
            var matches = FindDriveExePaths(asciiText);
            if (matches.Count > 0)
            {
                exePath = matches[^1];
                return true;
            }

            var utf16Text = Encoding.Unicode.GetString(bytes);
            matches = FindDriveExePaths(utf16Text);
            if (matches.Count > 0)
            {
                exePath = matches[^1];
                return true;
            }
        }
        catch { }

        return false;
    }

    private static List<string> FindDriveExePaths(string rawText)
    {
        var results = new List<string>();
        var idx = 0;
        while (idx + 6 < rawText.Length)
        {
            var colonSlash = rawText.IndexOf(@":\", idx, StringComparison.Ordinal);
            if (colonSlash <= 0)
            {
                break;
            }

            var driveChar = rawText[colonSlash - 1];
            if (!char.IsAsciiLetter(driveChar))
            {
                idx = colonSlash + 2;
                continue;
            }

            var start = colonSlash - 1;
            var end = start;
            while (end < rawText.Length && rawText[end] >= 32 && rawText[end] != '"' && rawText[end] != '<' && rawText[end] != '>' && rawText[end] != '|')
            {
                end++;
            }

            var candidate = rawText[start..end];
            var exePos = candidate.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exePos > 0)
            {
                var fullCandidate = candidate[..(exePos + 4)];
                if (!fullCandidate.Contains("Windows\\Installer", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(fullCandidate))
                {
                    results.Add(fullCandidate);
                }
            }

            idx = colonSlash + 2;
        }

        return results;
    }

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
                                    .Where(f => !t_ignoredExeNames.Contains(Path.GetFileName(f)))
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

    private static string GetCachedIconPath(string exePath, string iconsDir)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exePath.ToLowerInvariant())))[..16];
        return Path.Combine(iconsDir, $"win_{hash}.png");
    }

    private static string? GetOrExtractIcon(string exePath, string iconsDir)
    {
        var iconPath = GetCachedIconPath(exePath, iconsDir);
        return File.Exists(iconPath) || TryExtractIconPng(exePath, iconPath) ? iconPath : null;
    }

    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int FIcon;
        public uint XHotspot;
        public uint YHotspot;
        public IntPtr HbmMask;
        public IntPtr HbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int BmType;
        public int BmWidth;
        public int BmHeight;
        public int BmWidthBytes;
        public ushort BmPlanes;
        public ushort BmBitsPixel;
        public IntPtr BmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint BiSize;
        public int BiWidth;
        public int BiHeight;
        public ushort BiPlanes;
        public ushort BiBitCount;
        public uint BiCompression;
        public uint BiSizeImage;
        public int BiXPelsPerMeter;
        public int BiYPelsPerMeter;
        public uint BiClrUsed;
        public uint BiClrImportant;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SHGetFileInfoW(
        string pszPath,
        uint dwFileAttributes,
        ref byte psfi,
        uint cbFileInfo,
        uint uFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr GetDC(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial int GetObjectW(IntPtr hObject, int nCount, out BITMAP lpObject);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial int GetDIBits(
        IntPtr hdc,
        IntPtr hbmp,
        uint uStartScan,
        uint cScanLines,
        IntPtr lpvBits,
        ref BITMAPINFOHEADER lpbmi,
        uint uUsage);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr hObject);

    private static bool TryExtractIconPng(string exePath, string outputPngPath)
    {
        var shfiSize = (uint)(IntPtr.Size + 8 + 520 + 160);
        Span<byte> shfiBytes = stackalloc byte[704];
        shfiBytes.Clear();

        if (SHGetFileInfoW(exePath, 0, ref MemoryMarshal.GetReference(shfiBytes), shfiSize, ShgfiIcon | ShgfiLargeIcon) == IntPtr.Zero)
        {
            return false;
        }

        var hIcon = MemoryMarshal.Read<IntPtr>(shfiBytes);
        if (hIcon == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!GetIconInfo(hIcon, out var iconInfo))
            {
                return false;
            }

            try
            {
                if (iconInfo.HbmColor == IntPtr.Zero)
                {
                    return false;
                }

                if (GetObjectW(iconInfo.HbmColor, Marshal.SizeOf<BITMAP>(), out var bmp) == 0 ||
                    bmp.BmWidth <= 0 || bmp.BmHeight <= 0)
                {
                    return false;
                }

                var width = bmp.BmWidth;
                var height = bmp.BmHeight;

                var bmi = new BITMAPINFOHEADER
                {
                    BiSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    BiWidth = width,
                    BiHeight = -height,
                    BiPlanes = 1,
                    BiBitCount = 32,
                    BiCompression = 0
                };

                var hdc = GetDC(IntPtr.Zero);
                if (hdc == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    using var skBitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                    var pixelsPtr = skBitmap.GetPixels();
                    if (pixelsPtr == IntPtr.Zero)
                    {
                        return false;
                    }

                    var lines = GetDIBits(hdc, iconInfo.HbmColor, 0, (uint)height, pixelsPtr, ref bmi, 0);
                    if (lines <= 0)
                    {
                        return false;
                    }

                    using var image = SKImage.FromBitmap(skBitmap);
                    using var data = image.Encode(SKEncodedImageFormat.Png, 90);
                    if (data is null)
                    {
                        return false;
                    }

                    using var fs = File.OpenWrite(outputPngPath);
                    data.SaveTo(fs);
                    return true;
                }
                finally
                {
                    _ = ReleaseDC(IntPtr.Zero, hdc);
                }
            }
            finally
            {
                if (iconInfo.HbmColor != IntPtr.Zero)
                {
                    _ = DeleteObject(iconInfo.HbmColor);
                }

                if (iconInfo.HbmMask != IntPtr.Zero)
                {
                    _ = DeleteObject(iconInfo.HbmMask);
                }
            }
        }
        finally
        {
            _ = DestroyIcon(hIcon);
        }
    }
}
