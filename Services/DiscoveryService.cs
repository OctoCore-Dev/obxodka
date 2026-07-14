namespace obxodka.Services;

public class HydraConfig
{
    public string ActiveBridge { get; set; } = "https://obxodka.one";
    public DateTime UpdatedAt { get; set; }
}

public class DiscoveryService
{
    // The public Gist URL containing the hydra.json configuration.
    // Example: https://gist.githubusercontent.com/username/gist_id/raw/hydra.json
    // The user needs to populate this with their actual Gist raw URL.
    private const string GistUrl = "https://gist.githubusercontent.com/irovbyte/4f1063b597cba0a716f29431424c9d4e/raw/hydra.json";

    private static readonly HttpClient t_httpClient = new();
    private static HydraConfig? t_cachedConfig;
    private static readonly SemaphoreSlim t_fetchLock = new(1, 1);
    private static readonly JsonSerializerOptions t_jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task<string> GetActiveBridgeUrlAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        var gistUrl = GistUrl;
        if (gistUrl == "INSERT_GIST_RAW_URL_HERE")
        {
            // If the user hasn't set up the Gist yet, fallback to the hardcoded domain
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
            // Double-check after acquiring lock
            if (!forceRefresh && t_cachedConfig != null)
            {
                var host = new Uri(t_cachedConfig.ActiveBridge).Host;
                return host;
            }

            Debug.WriteLine("[DISCOVERY] Fetching latest Hydra config from Gist...");

            // Appending a random query parameter to bypass cache
            var url = $"{GistUrl}?t={DateTime.UtcNow.Ticks}";
            var response = await t_httpClient.GetAsync(url, ct);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                t_cachedConfig = JsonSerializer.Deserialize<HydraConfig>(json, t_jsonOptions);

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

        // Fallback to the main domain if discovery totally fails
        return t_cachedConfig != null ? new Uri(t_cachedConfig.ActiveBridge).Host : "obxodka.one";
    }
}

