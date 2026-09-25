namespace obxodka.Services;

public sealed class DiscoveryService
{
    private const string GistUrl = "https://gist.githubusercontent.com/irovbyte/4f1063b597cba0a716f29431424c9d4e/raw/hydra.json";

    private static readonly SocketsHttpHandler t_handler = new()
    {
        UseProxy = false
    };
    private static readonly HttpClient t_httpClient = new(t_handler) { Timeout = TimeSpan.FromSeconds(2) };
    private static HydraConfig? t_cachedConfig;
    private static readonly SemaphoreSlim t_fetchLock = new(1, 1);

    private static async Task<bool> IsHostResolvableAsync(string host, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host) || host.StartsWith("bridge-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IPAddress.TryParse(host, out _))
        {
            return true;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2.0));
            var addresses = await Dns.GetHostAddressesAsync(host, timeoutCts.Token).ConfigureAwait(false);
            return addresses.Any(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch
        {
            return false;
        }
    }

    public static async Task<string> GetActiveBridgeUrlAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh)
        {
            if (t_cachedConfig is { ActiveBridge: { Length: > 0 } cachedBridge })
            {
                var cachedHost = new Uri(cachedBridge).Host;
                if (!cachedHost.StartsWith("bridge-", StringComparison.OrdinalIgnoreCase) && await IsHostResolvableAsync(cachedHost, ct).ConfigureAwait(false))
                {
                    return cachedHost;
                }

                t_cachedConfig = null;
            }

            try
            {
                var savedBridge = Preferences.Default.Get("cached_bridge_host", string.Empty);
                if (!string.IsNullOrWhiteSpace(savedBridge))
                {
                    if (!savedBridge.StartsWith("bridge-", StringComparison.OrdinalIgnoreCase) && await IsHostResolvableAsync(savedBridge, ct).ConfigureAwait(false))
                    {
                        return savedBridge;
                    }

                    Preferences.Default.Remove("cached_bridge_host");
                }
            }
            catch { }
        }

        await t_fetchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && t_cachedConfig is { ActiveBridge: { Length: > 0 } readyBridge })
            {
                var readyHost = new Uri(readyBridge).Host;
                if (!readyHost.StartsWith("bridge-", StringComparison.OrdinalIgnoreCase) && await IsHostResolvableAsync(readyHost, ct).ConfigureAwait(false))
                {
                    return readyHost;
                }
            }

            Debug.WriteLine("[DISCOVERY] Fetching latest Hydra config from Gist...");
            var url = $"{GistUrl}?t={DateTime.UtcNow.Ticks}";
            var response = await t_httpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                t_cachedConfig = JsonSerializer.Deserialize(json, AppJsonContext.Default.HydraConfig);
                if (t_cachedConfig is { ActiveBridge: { Length: > 0 } bridge })
                {
                    var host = new Uri(bridge).Host;
                    if (await IsHostResolvableAsync(host, ct).ConfigureAwait(false))
                    {
                        try
                        {
                            Preferences.Default.Set("cached_bridge_host", host);
                        }
                        catch { }

                        Debug.WriteLine($"[DISCOVERY] Successfully resolved and verified active bridge: {host}");
                        return host;
                    }
                    else
                    {
                        Debug.WriteLine($"[DISCOVERY] Host from Gist '{host}' failed DNS resolution. Discarding.");
                        t_cachedConfig = null;
                    }
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

        try
        {
            var fallbackBridge = Preferences.Default.Get("cached_bridge_host", string.Empty);
            if (!string.IsNullOrWhiteSpace(fallbackBridge) &&
                !fallbackBridge.StartsWith("bridge-", StringComparison.OrdinalIgnoreCase) &&
                await IsHostResolvableAsync(fallbackBridge, ct).ConfigureAwait(false))
            {
                return fallbackBridge;
            }

            Preferences.Default.Remove("cached_bridge_host");
        }
        catch { }

        return "obxodka.one";
    }
}

