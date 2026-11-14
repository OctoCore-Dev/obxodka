namespace obxodka.Shared.Themes;

public sealed partial class ThemeStorageService
{
    //список зеркал для скачивания содержимого тем
    private static readonly string[] s_themesBaseUrls =
    [
        "https://raw.githubusercontent.com/OctoCore-Dev/themes/main/themes/",
        "https://cdn.jsdelivr.net/gh/OctoCore-Dev/themes@main/themes/",
        "https://fastly.jsdelivr.net/gh/OctoCore-Dev/themes@main/themes/",
        "https://gcore.jsdelivr.net/gh/OctoCore-Dev/themes@main/themes/"
    ];

    //единый переиспользуемый http клиент
    private static readonly HttpClient s_httpClient = CreateHttpClient();

    private readonly ConcurrentDictionary<string, Task<bool>> _activeDownloads = new();
    private readonly ConcurrentDictionary<string, double> _downloadProgress = new();

    //создание настроенного http клиента
    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OctoCore-Client/1.0 (Windows NT 10.0; Win64; x64)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        return client;
    }

    //проверка идет ли скачивание темы в данный момент
    public bool IsThemeDownloading(string themeId, out double progress)
    {
        if (_activeDownloads.ContainsKey(themeId))
        {
            progress = _downloadProgress.TryGetValue(themeId, out var p) ? p : 0.0;
            return true;
        }

        progress = 0.0;
        return false;
    }

    //скачивание и безопасная установка темы из удаленных зеркал
    public Task<bool> DownloadAndInstallThemeAsync(
        string themeId,
        IProgress<double>? progress = null,
        Action<string, double, bool, bool, string?>? onProgressChanged = null,
        CancellationToken ct = default)
    {
        if (!ThemeValidator.IsSafeId(themeId))
        {
            LastError = $"[ThemeSecurity] Недопустимый идентификатор темы: '{themeId}'";
            return Task.FromResult(false);
        }

        lock (_activeDownloads)
        {
            if (_activeDownloads.TryGetValue(themeId, out var runningTask))
            {
                return runningTask;
            }

            var task = RunDownloadAndInstallThemeInternalAsync(themeId, progress, onProgressChanged, ct);
            _activeDownloads[themeId] = task;
            return task;
        }
    }

    //внутренняя процедура скачивания ассетов темы с оповещением прогресса
    private async Task<bool> RunDownloadAndInstallThemeInternalAsync(
        string themeId,
        IProgress<double>? progress,
        Action<string, double, bool, bool, string?>? onProgressChanged,
        CancellationToken ct)
    {
        var targetDir = Path.Combine(ThemesDirectory, themeId.TrimStart('/', '\\'));
        _ = Directory.CreateDirectory(targetDir);

        void Report(double p)
        {
            _downloadProgress[themeId] = p;
            progress?.Report(p);
            onProgressChanged?.Invoke(themeId, p, false, false, null);
        }

        var success = false;
        try
        {
            Report(0.1);

            var manifestJson = await DownloadStringWithMirrorsAsync(themeId, "theme.json", ct).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize(manifestJson, ThemeJsonContext.Default.ThemeManifest);

            string? validationError = null;
            if (manifest == null || !ThemeValidator.ValidateManifest(manifest, out validationError))
            {
                throw new SecurityException($"Invalid theme manifest: {validationError ?? "Manifest is empty"}");
            }

            Report(0.3);

            var assetPaths = EnumerateAssetPaths(manifest).ToList();
            var totalAssets = assetPaths.Count;
            var processed = 0;

            foreach (var rel in assetPaths)
            {
                var isIcon = string.Equals(rel, "icon.png", StringComparison.OrdinalIgnoreCase);
                var dest = Path.Combine(targetDir, rel.TrimStart('/', '\\'));
                var parentDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                {
                    _ = Directory.CreateDirectory(parentDir);
                }

                await DownloadThemeAssetWithMirrorsAsync(themeId, rel, dest, ct, isRequired: isIcon).ConfigureAwait(false);
                processed++;
                Report(0.3 + (0.65 * processed / Math.Max(1, totalAssets)));
            }

            await File.WriteAllTextAsync(Path.Combine(targetDir, "theme.json"), manifestJson, ct).ConfigureAwait(false);

            Report(1.0);
            success = true;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            if (Directory.Exists(targetDir))
            {
                try
                {
                    Directory.Delete(targetDir, true);
                }
                catch
                {
                    //подавление ошибок очистки
                }
            }

            return false;
        }
        finally
        {
            lock (_activeDownloads)
            {
                _ = _activeDownloads.TryRemove(themeId, out _);
                _ = _downloadProgress.TryRemove(themeId, out _);
            }

            var finalSuccess = success;
            var finalErr = success ? null : LastError;
            onProgressChanged?.Invoke(themeId, 1.0, true, finalSuccess, finalErr);
        }
    }

    //скачивание текстового файла темы с перебором зеркал
    private static async Task<string> DownloadStringWithMirrorsAsync(string themeId, string relativePath, CancellationToken ct)
    {
        var cleanRelPath = relativePath.Replace('\\', '/').TrimStart('/');
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Exception? lastEx = null;

        foreach (var baseUrl in s_themesBaseUrls)
        {
            try
            {
                var separator = cleanRelPath.Contains('?') ? "&" : "?";
                var url = $"{baseUrl}{themeId}/{cleanRelPath}{separator}_t={timestamp}";
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.CacheControl = new CacheControlHeaderValue
                {
                    NoCache = true,
                    NoStore = true,
                    MustRevalidate = true
                };

                using var res = await s_httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                if (res.IsSuccessStatusCode)
                {
                    return await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                lastEx = ex;
            }
        }

        throw lastEx ?? new InvalidOperationException($"Не удалось загрузить '{cleanRelPath}' темы '{themeId}' со всех зеркал.");
    }

    //потоковое скачивание ресурса темы напрямую на диск без выделения буфера в ram
    private static async Task DownloadThemeAssetWithMirrorsAsync(
        string themeId,
        string relativePath,
        string destinationPath,
        CancellationToken ct,
        bool isRequired = true)
    {
        if (!ThemeValidator.IsSafeRelativePath(relativePath))
        {
            throw new SecurityException($"[ThemeSecurity] Блокировка: небезопасный относительный путь ресурса '{relativePath}'");
        }

        var cleanRelPath = relativePath.Replace('\\', '/').TrimStart('/');
        var ext = Path.GetExtension(cleanRelPath);
        if (!SafeThemeExtractor.IsAllowedExtension(ext))
        {
            throw new SecurityException($"[ThemeSecurity] Блокировка: запрещенное расширение '{ext}' у ресурса '{cleanRelPath}'");
        }

        var fullDest = Path.GetFullPath(destinationPath);
        var parentDir = Path.GetDirectoryName(fullDest);
        if (!string.IsNullOrEmpty(parentDir))
        {
            _ = Directory.CreateDirectory(parentDir);
        }

        var maxAllowedSize = SafeThemeExtractor.GetMaxFileSize(ext);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Exception? lastEx = null;

        foreach (var baseUrl in s_themesBaseUrls)
        {
            var separator = cleanRelPath.Contains('?') ? "&" : "?";
            var url = $"{baseUrl}{themeId}/{cleanRelPath}{separator}_t={timestamp}";

            try
            {
                if (await TryDownloadAssetFromUrlAsync(url, cleanRelPath, fullDest, ext, maxAllowedSize, ct).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (SecurityException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastEx = ex;
            }
        }

        if (isRequired)
        {
            throw lastEx ?? new InvalidOperationException($"Не удалось скачать обязательный ресурс '{relativePath}' темы '{themeId}' со всех доступных зеркал.");
        }
    }

    //попытка скачивания ассета с одного конкретного зеркала
    private static async Task<bool> TryDownloadAssetFromUrlAsync(
        string url,
        string cleanRelPath,
        string fullDest,
        string ext,
        long maxAllowedSize,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true,
            MustRevalidate = true
        };

        using var response = await s_httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        if (response.Content.Headers.ContentLength is { } len && len > maxAllowedSize)
        {
            throw new SecurityException($"[ThemeSecurity] Размер ресурса '{cleanRelPath}' ({len} байт) превышает лимит {maxAllowedSize} байт!");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);

        //чтение первых 64 байт для валидации magic-bytes перед записью
        var headerBuffer = new byte[64];
        var headerRead = 0;
        while (headerRead < headerBuffer.Length)
        {
            var r = await responseStream.ReadAsync(headerBuffer.AsMemory(headerRead, headerBuffer.Length - headerRead), cts.Token).ConfigureAwait(false);
            if (r == 0)
            {
                break;
            }

            headerRead += r;
        }

        if (headerRead == 0)
        {
            throw new SecurityException($"[ThemeSecurity] Скачанный ресурс '{cleanRelPath}' пуст (0 байт).");
        }

        if (!SafeThemeExtractor.VerifyMagicBytes(ext, headerBuffer, headerRead))
        {
            throw new SecurityException($"[ThemeSecurity] Ошибка валидации сигнатуры ресурса '{cleanRelPath}' (формат '{ext}').");
        }

        try
        {
            await using var fileStream = new FileStream(fullDest, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            await fileStream.WriteAsync(headerBuffer.AsMemory(0, headerRead), ct).ConfigureAwait(false);

            long totalRead = headerRead;
            var copyBuffer = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                int bytesRead;
                while ((bytesRead = await responseStream.ReadAsync(copyBuffer.AsMemory(0, copyBuffer.Length), cts.Token).ConfigureAwait(false)) > 0)
                {
                    totalRead += bytesRead;
                    if (totalRead > maxAllowedSize)
                    {
                        throw new SecurityException($"[ThemeSecurity] Размер ресурса '{cleanRelPath}' превысил лимит {maxAllowedSize} байт!");
                    }

                    if (SafeThemeExtractor.ContainsPolyglotPayload(copyBuffer.AsSpan(0, bytesRead)))
                    {
                        throw new SecurityException($"[ThemeSecurity] Блокировка полиглот-данных в '{cleanRelPath}'");
                    }

                    await fileStream.WriteAsync(copyBuffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(copyBuffer);
            }

            return true;
        }
        catch
        {
            if (File.Exists(fullDest))
            {
                try
                {
                    File.Delete(fullDest);
                }
                catch
                {
                    //подавление ошибок удаления поврежденного файла
                }
            }

            throw;
        }
    }
}
