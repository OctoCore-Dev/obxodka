namespace obxodka.Shared.Themes;

public sealed partial class ThemeStorageService
{
    //импорт и безопасная распаковка архива темы
    public async Task<(bool Success, string? ErrorMessage, ThemeManifest? Manifest)> ImportThemeFromZipAsync(
        Stream zipStream,
        string fileName,
        CancellationToken ct = default)
    {
        if (zipStream == null)
        {
            return (false, "Поток архива темы пуст.", null);
        }

        var tempDir = Path.Join(Path.GetTempPath(), "obxodka_theme_import_" + Guid.NewGuid().ToString("N"));
        try
        {
            await SafeThemeExtractor.ExtractZipSafelyAsync(zipStream, tempDir, ct).ConfigureAwait(false);

            var sourceThemeDir = tempDir;
            var manifestPath = Path.Join(tempDir, "theme.json");
            if (!File.Exists(manifestPath))
            {
                var subDirs = Directory.GetDirectories(tempDir);
                foreach (var subDir in subDirs)
                {
                    var candidate = Path.Join(subDir, "theme.json");
                    if (File.Exists(candidate))
                    {
                        sourceThemeDir = subDir;
                        manifestPath = candidate;
                        break;
                    }
                }
            }

            if (!File.Exists(manifestPath))
            {
                return (false, $"В архиве '{fileName}' не найден файл theme.json.", null);
            }

            var manifestJson = await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize(manifestJson, ThemeJsonContext.Default.ThemeManifest);
            string? manifestError = null;
            if (manifest == null || !ThemeValidator.ValidateManifest(manifest, out manifestError))
            {
                return (false, $"Некорректный манифест темы: {manifestError ?? "Пустой манифест"}", null);
            }

            foreach (var extractedFile in Directory.EnumerateFiles(sourceThemeDir, "*", SearchOption.AllDirectories))
            {
                if (!ThemeValidator.ValidateAssetFile(extractedFile, out var assetError))
                {
                    var relName = Path.GetRelativePath(sourceThemeDir, extractedFile);
                    return (false, $"Файл '{relName}' не прошел проверку безопасности: {assetError}", null);
                }
            }

            var targetDir = Path.Join(ThemesDirectory, manifest.Id.TrimStart('/', '\\'));
            if (Directory.Exists(targetDir))
            {
                Directory.Delete(targetDir, true);
            }

            _ = Directory.CreateDirectory(targetDir);
            foreach (var srcFile in Directory.EnumerateFiles(sourceThemeDir, "*", SearchOption.AllDirectories))
            {
                var relPath = Path.GetRelativePath(sourceThemeDir, srcFile);
                if (!ThemeValidator.IsSafeRelativePath(relPath))
                {
                    return (false, $"Недопустимый относительный путь файла: '{relPath}'", null);
                }

                var destFile = Path.Join(targetDir, relPath.TrimStart('/', '\\'));
                var destParent = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destParent))
                {
                    _ = Directory.CreateDirectory(destParent);
                }

                File.Copy(srcFile, destFile, true);
            }

            return (true, null, manifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidDataException
                                       or JsonException or OperationCanceledException or NotSupportedException or ArgumentException)
        {
            LastError = ex.Message;
            return (false, ex.Message, null);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
                {
                    //подавление ошибок удаления временной директории
                }
            }
        }
    }
}
