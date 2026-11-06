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

        var tempDir = Path.Combine(Path.GetTempPath(), "obxodka_theme_import_" + Guid.NewGuid().ToString("N"));
        try
        {
            await SafeThemeExtractor.ExtractZipSafelyAsync(zipStream, tempDir, ct).ConfigureAwait(false);

            var sourceThemeDir = tempDir;
            var manifestPath = Path.Combine(tempDir, "theme.json");
            if (!File.Exists(manifestPath))
            {
                var subDirs = Directory.GetDirectories(tempDir);
                foreach (var subDir in subDirs)
                {
                    var candidate = Path.Combine(subDir, "theme.json");
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

            var targetDir = Path.Combine(ThemesDirectory, manifest.Id);
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

                var destFile = Path.Combine(targetDir, relPath);
                var destParent = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destParent))
                {
                    _ = Directory.CreateDirectory(destParent);
                }

                File.Copy(srcFile, destFile, overwrite: true);
            }

            return (true, null, manifest);
        }
        catch (Exception ex)
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
                catch
                {
                    //подавление ошибок удаления временной директории
                }
            }
        }
    }
}
