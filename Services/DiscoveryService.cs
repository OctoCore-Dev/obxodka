namespace obxodka.Services;

public class HydraConfig
{
    public string ActiveBridge { get; set; } = "https://obxodka.one";
    public DateTime UpdatedAt { get; set; }
}

public class DiscoveryService
{
    private const string GistUrl = "https://gist.githubusercontent.com/irovbyte/4f1063b597cba0a716f29431424c9d4e/raw/hydra.json";

    private static readonly SocketsHttpHandler t_handler = new()
    {
        UseProxy = false
    };
    private static readonly HttpClient t_httpClient = new(t_handler) { Timeout = TimeSpan.FromSeconds(5) };
    private static HydraConfig? t_cachedConfig;
    private static readonly SemaphoreSlim t_fetchLock = new(1, 1);

    public static async Task<string> GetActiveBridgeUrlAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        var gistUrl = GistUrl;
        if (gistUrl == "INSERT_GIST_RAW_URL_HERE")
        {
            return "obxodka.one";
        }

        if (!forceRefresh && t_cachedConfig != null)
        {
            var host = new Uri(t_cachedConfig.ActiveBridge).Host;
            return host;
        }

        await t_fetchLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && t_cachedConfig != null)
            {
                var host = new Uri(t_cachedConfig.ActiveBridge).Host;
                return host;
            }

            Debug.WriteLine("[DISCOVERY] Fetching latest Hydra config from Gist...");
            var url = $"{GistUrl}?t={DateTime.UtcNow.Ticks}";
            var response = await t_httpClient.GetAsync(url, ct);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                t_cachedConfig = JsonSerializer.Deserialize(json, AppJsonContext.Default.HydraConfig);
                if (t_cachedConfig != null && !string.IsNullOrEmpty(t_cachedConfig.ActiveBridge))
                {
                    Debug.WriteLine($"[DISCOVERY] Successfully resolved active bridge: {t_cachedConfig.ActiveBridge}");
                    var host = new Uri(t_cachedConfig.ActiveBridge).Host;
                    return host;
                }
            }

            Debug.WriteLine($"[DISCOVERY] Failed to fetch config. Status: {response.StatusCode}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DISCOVERY] Exception during fetch: {ex.Message}");
        }
        finally
        {
            _ = t_fetchLock.Release();
        }
        return t_cachedConfig != null ? new Uri(t_cachedConfig.ActiveBridge).Host : "obxodka.one";
    }
}
