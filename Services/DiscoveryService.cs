namespace obxodka.Services;

public sealed class DiscoveryService
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
        if (!forceRefresh && t_cachedConfig is not null)
        {
            return new Uri(t_cachedConfig.ActiveBridge).Host;
        }

        await t_fetchLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && t_cachedConfig is not null)
            {
                return new Uri(t_cachedConfig.ActiveBridge).Host;
            }

            Debug.WriteLine("[DISCOVERY] Fetching latest Hydra config from Gist...");
            var url = $"{GistUrl}?t={DateTime.UtcNow.Ticks}";
            var response = await t_httpClient.GetAsync(url, ct);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                t_cachedConfig = JsonSerializer.Deserialize(json, AppJsonContext.Default.HydraConfig);
                if (t_cachedConfig is { ActiveBridge: { Length: > 0 } bridge })
                {
                    Debug.WriteLine($"[DISCOVERY] Successfully resolved active bridge: {bridge}");
                    return new Uri(bridge).Host;
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

        return t_cachedConfig is not null ? new Uri(t_cachedConfig.ActiveBridge).Host : "obxodka.one";
    }
}
