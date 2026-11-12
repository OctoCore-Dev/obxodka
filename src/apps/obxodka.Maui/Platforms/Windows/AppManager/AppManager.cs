namespace obxodka.Platforms.Windows;

//управление политиками и списком исключений приложений для windows
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

    private List<AppInfoItem>? _cachedApps;

    private static readonly HashSet<string> s_ignoredExeNames = new(
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

    private static readonly string[] s_defaultBypassedExes =
    [
        "AnyDesk.exe",
        "RustDesk.exe",
        "TeamViewer.exe",
        "qbittorrent.exe",
        "uTorrent.exe",
        "Transmission-qt.exe"
    ];

    //проверка является ли приложение самой обходкой или защищенным процессом
    public static bool IsSelfOrProtectedApp(string? exePath, string? exeName = null, string? friendlyName = null)
    {
        if (Core.Models.SplitTunnelPolicy.IsProtectedObxodkaEntry(exePath) ||
            Core.Models.SplitTunnelPolicy.IsProtectedObxodkaEntry(exeName) ||
            Core.Models.SplitTunnelPolicy.IsProtectedObxodkaEntry(friendlyName))
        {
            return true;
        }

        var selfProcessPath = Environment.ProcessPath;
        return !string.IsNullOrWhiteSpace(selfProcessPath) &&
               !string.IsNullOrWhiteSpace(exePath) &&
               string.Equals(exePath, selfProcessPath, StringComparison.OrdinalIgnoreCase);
    }

    //синхронизация политик раздельного туннелирования с wfp движком
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
        policy.BypassRuMedia = Preferences.Get(PresetMediaKey, true);
        policy.BypassRuMarketplaces = Preferences.Get(PresetMarketKey, true);
        policy.BypassAllRuTlds = Preferences.Get(PresetRuTldsKey, true);

        var bypassedPackages = LoadBypassedPackagesList();
        policy.SetBypassedExecutables(bypassedPackages);

        var customRules = GetCustomRules();
        policy.SetCustomRules(customRules);

        _ = Task.Run(WindowsSplitTunnelEngine.ActivateOrReloadAsync);
    }

    //чтение пользовательских правил доменов и ip адресов
    public static List<string> GetCustomRules()
    {
        var raw = Preferences.Get(CustomRulesKey, string.Empty);
        return string.IsNullOrWhiteSpace(raw)
            ? []
            : [.. raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(r => !Core.Models.SplitTunnelPolicy.IsProtectedObxodkaEntry(r))];
    }

    //сохранение пользовательских правил доменов и ip адресов
    public static void SaveCustomRules(IEnumerable<string> rules)
    {
        var normalized = rules
            .Select(Core.Models.SplitTunnelPolicy.NormalizeRuleEntry)
            .Where(r => !string.IsNullOrWhiteSpace(r) && !Core.Models.SplitTunnelPolicy.IsProtectedObxodkaEntry(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Preferences.Set(CustomRulesKey, string.Join("|", normalized));
        SyncPolicyFromPreferences();
    }

    //загрузка списка исключений из хранилища настроек
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

        return [.. s_defaultBypassedExes];
    }

    //получение списка исключенных пакетов
    public List<string> GetBypassedPackages() => LoadBypassedPackagesList();

    //сохранение списка исключенных пакетов
    public void SaveBypassedPackages(List<string> packages)
    {
        var filtered = packages
            .Where(p => !string.IsNullOrWhiteSpace(p) && !IsSelfOrProtectedApp(p, p))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        Preferences.Set(BypassedAppsKey, string.Join("|", filtered));
        SyncPolicyFromPreferences();
    }

    //получение сохраненных путей пользовательских исполняемых файлов
    public static List<string> GetCustomExePaths()
    {
        var saved = Preferences.Get(CustomExesKey, string.Empty);
        return string.IsNullOrWhiteSpace(saved)
            ? []
            : [.. saved.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(p => !IsSelfOrProtectedApp(p))];
    }

    //добавление пользовательского исполняемого файла в список исключений
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

        var existing = _cachedApps?.FirstOrDefault(a => a.PackageName.Equals(exeName, StringComparison.OrdinalIgnoreCase));
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
        _cachedApps?.Insert(0, item);
        return item;
    }
}
