namespace obxodka.Config;

public static class AppConfig
{
    public const string DefaultApiBaseUrl = "https://obxodka.one/";
    public static string BaseUrl => ApiBaseUrl;

    private static string GetInitialBaseUrl()
    {
        try
        {
            var savedBridge = Preferences.Default.Get("cached_bridge_host", string.Empty);
            if (!string.IsNullOrWhiteSpace(savedBridge))
            {
                if (IPAddress.TryParse(savedBridge, out _))
                {
                    return $"https://{savedBridge}/";
                }

                var entry = Dns.GetHostEntry(savedBridge);
                if (entry.AddressList.Length > 0)
                {
                    return $"https://{savedBridge}/";
                }
            }
        }
        catch
        {
            try
            {
                Preferences.Default.Remove("cached_bridge_host");
            }
            catch { }
        }

        return DefaultApiBaseUrl;
    }

    public static string ApiBaseUrl
    {
        get;
        set => field = string.IsNullOrWhiteSpace(value)
            ? DefaultApiBaseUrl
            : value.Trim();
    } = GetInitialBaseUrl();

    public static string ApiUrl(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        return $"{ApiBaseUrl.AsSpan().TrimEnd('/')}/{endpoint.AsSpan().TrimStart('/')}";
    }

    public static string ApiUrl(ReadOnlySpan<char> endpoint)
    {
        return endpoint.IsWhiteSpace()
            ? throw new ArgumentException("Endpoint cannot be empty.", nameof(endpoint))
            : $"{ApiBaseUrl.AsSpan().TrimEnd('/')}/{endpoint.TrimStart('/')}";
    }
}
