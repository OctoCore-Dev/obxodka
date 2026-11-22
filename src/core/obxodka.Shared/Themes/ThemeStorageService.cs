namespace obxodka.Shared.Themes;

//сервис управления локальным хранилищем тем, распаковки и загрузки ассетов
public sealed partial class ThemeStorageService
{
    //идентификатор встроенной сезонной хэллоуин темы
    public const string SeasonalHalloweenThemeId = "halloween_seasonal_default";

    public ThemeStorageService(string? themesDirectory = null)
    {
        ThemesDirectory = themesDirectory ?? GetDefaultThemesDirectory();
        _ = Directory.CreateDirectory(ThemesDirectory);
    }

    //путь к директории хранения установленных тем
    public string ThemesDirectory { get; }

    //последняя зафиксированная ошибка операций
    public string? LastError { get; private set; }

    //получение стандартной директории тем в данных приложения
    public static string GetDefaultThemesDirectory() =>
        Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "obxodka", "themes");

    //получение списка всех корректно установленных тем
    public List<ThemeManifest> GetInstalledThemes()
    {
        var list = new List<ThemeManifest>();
        if (!Directory.Exists(ThemesDirectory))
        {
            return list;
        }

        foreach (var manifestPath in Directory.GetDirectories(ThemesDirectory)
                     .Select(dir => Path.Join(dir, "theme.json"))
                     .Where(File.Exists))
        {
            try
            {
                var json = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize(json, ThemeJsonContext.Default.ThemeManifest);
                if (manifest != null && ThemeValidator.ValidateManifest(manifest, out _))
                {
                    list.Add(manifest);
                }
            }
            catch (IOException)
            {
                //битый или недоступный манифест пропускаем
            }
            catch (UnauthorizedAccessException)
            {
                //нет доступа к манифесту
            }
            catch (JsonException)
            {
                //поврежденный json манифеста пропускаем
            }
        }

        return list;
    }

    //безопасное разрешение директории темы внутри ThemesDirectory с защитой от path traversal
    public string? ResolveSafeThemeDirectory(string? themeId)
    {
        if (!ThemeValidator.IsSafeId(themeId))
        {
            return null;
        }

        var baseDir = Path.GetFullPath(ThemesDirectory);
        if (!baseDir.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            baseDir += Path.DirectorySeparatorChar;
        }

        var targetDir = Path.GetFullPath(Path.Join(baseDir, themeId!.Trim()));
        return targetDir.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase) && targetDir.Length > baseDir.Length
            ? targetDir
            : null;
    }

    //проверка наличия установленной темы по id
    public bool IsThemeInstalled(string themeId)
    {
        var targetDir = ResolveSafeThemeDirectory(themeId);
        return targetDir != null && File.Exists(Path.Join(targetDir, "theme.json"));
    }

    //чтение локальной версии установленной темы
    public string? GetInstalledThemeVersion(string themeId)
    {
        var targetDir = ResolveSafeThemeDirectory(themeId);
        if (targetDir == null)
        {
            return null;
        }

        var manifestPath = Path.Join(targetDir, "theme.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize(json, ThemeJsonContext.Default.ThemeManifest);
            return manifest?.Version;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    //проверка доступности обновления темы
    public bool IsUpdateAvailable(string themeId, string? remoteVersion)
    {
        var localVersion = GetInstalledThemeVersion(themeId);
        return localVersion != null && ThemeValidator.IsVersionNewer(localVersion, remoteVersion);
    }

    //удаление темы с диска
    public bool DeleteTheme(string themeId)
    {
        var targetDir = ResolveSafeThemeDirectory(themeId);
        if (targetDir == null)
        {
            return false;
        }

        try
        {
            if (Directory.Exists(targetDir))
            {
                Directory.Delete(targetDir, true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            LastError = ex.Message;
            return false;
        }
    }
}
