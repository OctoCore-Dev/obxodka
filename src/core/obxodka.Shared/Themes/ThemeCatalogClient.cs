namespace obxodka.Shared.Themes;

//клиент для загрузки и кэширования каталога тем с зеркал cdn
public static class ThemeCatalogClient
{
    //список зеркал для отказоустойчивой загрузки каталога
    private static readonly string[] s_catalogUrls =
    [
        "https://raw.githubusercontent.com/OctoCore-Dev/themes/main/catalog.json",
        "https://cdn.jsdelivr.net/gh/OctoCore-Dev/themes@main/catalog.json",
        "https://fastly.jsdelivr.net/gh/OctoCore-Dev/themes@main/catalog.json",
        "https://gcore.jsdelivr.net/gh/OctoCore-Dev/themes@main/catalog.json"
    ];

    //единый переиспользуемый клиент http
    private static readonly HttpClient s_httpClient = CreateHttpClient();

    //создание настроенного http клиента с таймаутом
    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OctoCore-Client/1.0 (Windows NT 10.0; Win64; x64)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        return client;
    }

    //преобразование ссылок github raw на cdn зеркало jsdelivr для обхода блокировок
    public static string ToMirrorUrl(string? rawUrl)
    {
        return string.IsNullOrWhiteSpace(rawUrl)
            ? string.Empty
            : rawUrl.StartsWith("https://raw.githubusercontent.com/OctoCore-Dev/themes/main/", StringComparison.OrdinalIgnoreCase)
            ? rawUrl.Replace(
                "https://raw.githubusercontent.com/OctoCore-Dev/themes/main/",
                "https://cdn.jsdelivr.net/gh/OctoCore-Dev/themes@main/",
                StringComparison.OrdinalIgnoreCase)
            : rawUrl;
    }

    //нормализация url иконок и превью каталога под cdn зеркала
    public static void NormalizeCatalogUrls(ThemeCatalogResponse catalog)
    {
        foreach (var theme in catalog.Themes)
        {
            theme.IconUrl = ToMirrorUrl(theme.IconUrl);
            theme.PreviewUrl = ToMirrorUrl(theme.PreviewUrl);
        }
    }

    //загрузка каталога с перебором зеркал с защитой от кэширования
    public static async Task<ThemeCatalogResponse?> FetchCatalogFromMirrorsAsync(CancellationToken ct = default)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var url in s_catalogUrls)
        {
            try
            {
                var separator = url.Contains('?') ? "&" : "?";
                var urlWithBuster = $"{url}{separator}_t={timestamp}";
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                using var req = new HttpRequestMessage(HttpMethod.Get, urlWithBuster);
                req.Headers.CacheControl = new CacheControlHeaderValue
                {
                    NoCache = true,
                    NoStore = true,
                    MustRevalidate = true
                };

                using var res = await s_httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                if (res.IsSuccessStatusCode)
                {
                    await using var stream = await res.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                    var catalog = await JsonSerializer.DeserializeAsync(stream, ThemeJsonContext.Default.ThemeCatalogResponse, cts.Token).ConfigureAwait(false);
                    if (catalog != null && catalog.Themes.Count > 0)
                    {
                        return catalog;
                    }
                }
            }
            catch
            {
                //переход к следующему зеркалу при ошибке
            }
        }

        return null;
    }

    //загрузка каталога с поддержкой дискового кэша и фонового обновления
    public static async Task<ThemeCatalogResponse> LoadCatalogAsync(
        string? cacheFilePath = null,
        bool forceRefresh = false,
        Action<ThemeCatalogResponse>? onBackgroundCatalogUpdated = null,
        CancellationToken ct = default)
    {
        if (forceRefresh && !string.IsNullOrWhiteSpace(cacheFilePath) && File.Exists(cacheFilePath))
        {
            try
            {
                File.Delete(cacheFilePath);
            }
            catch
            {
                //игнорируем ошибку удаления кэша
            }
        }

        var isCacheStale = string.IsNullOrWhiteSpace(cacheFilePath) ||
                            !File.Exists(cacheFilePath) ||
                            (DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFilePath)) > TimeSpan.FromMinutes(5);

        //чтение из свежего кэша с запуском фоновой проверки
        if (!forceRefresh && !isCacheStale && !string.IsNullOrWhiteSpace(cacheFilePath) && File.Exists(cacheFilePath))
        {
            try
            {
                var cachedJson = await File.ReadAllTextAsync(cacheFilePath, ct).ConfigureAwait(false);
                var cachedCatalog = JsonSerializer.Deserialize(cachedJson, ThemeJsonContext.Default.ThemeCatalogResponse);
                if (cachedCatalog?.Themes.Count > 0)
                {
                    NormalizeCatalogUrls(cachedCatalog);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var fresh = await FetchCatalogFromMirrorsAsync(CancellationToken.None).ConfigureAwait(false);
                            if (fresh != null && fresh.Themes.Count > 0)
                            {
                                NormalizeCatalogUrls(fresh);
                                await File.WriteAllTextAsync(cacheFilePath, JsonSerializer.Serialize(fresh, ThemeJsonContext.Default.ThemeCatalogResponse), CancellationToken.None).ConfigureAwait(false);
                                onBackgroundCatalogUpdated?.Invoke(fresh);
                            }
                        }
                        catch
                        {
                            //подавление ошибок фонового обновления
                        }
                    }, CancellationToken.None);

                    return cachedCatalog;
                }
            }
            catch
            {
                //при повреждении кэша переходим к сетевой загрузке
            }
        }

        //загрузка свежего каталога из сети
        try
        {
            var catalog = await FetchCatalogFromMirrorsAsync(ct).ConfigureAwait(false);
            if (catalog != null && catalog.Themes.Count > 0)
            {
                NormalizeCatalogUrls(catalog);
                if (!string.IsNullOrWhiteSpace(cacheFilePath))
                {
                    var parent = Path.GetDirectoryName(cacheFilePath);
                    if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    {
                        _ = Directory.CreateDirectory(parent);
                    }
                    await File.WriteAllTextAsync(cacheFilePath, JsonSerializer.Serialize(catalog, ThemeJsonContext.Default.ThemeCatalogResponse), ct).ConfigureAwait(false);
                }
                return catalog;
            }
        }
        catch
        {
            //ошибка сети, пробуем устаревший кэш
        }

        //резервное чтение из кэша любой давности
        if (!string.IsNullOrWhiteSpace(cacheFilePath) && File.Exists(cacheFilePath))
        {
            try
            {
                var cachedJson = await File.ReadAllTextAsync(cacheFilePath, ct).ConfigureAwait(false);
                var cachedCatalog = JsonSerializer.Deserialize(cachedJson, ThemeJsonContext.Default.ThemeCatalogResponse);
                if (cachedCatalog?.Themes.Count > 0)
                {
                    NormalizeCatalogUrls(cachedCatalog);
                    return cachedCatalog;
                }
            }
            catch
            {
                //кэш не читается
            }
        }

        return new ThemeCatalogResponse
        {
            SchemaVersion = "4.0.0",
            TotalThemes = 0,
            Themes = []
        };
    }
}
