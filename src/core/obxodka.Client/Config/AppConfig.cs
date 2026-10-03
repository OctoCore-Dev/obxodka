namespace obxodka.Config;

public static class AppConfig
{
    public const string DefaultApiBaseUrl = "https://obxodka.one/";
    public const string LegacyApiBaseUrl = "https://api.octocore.dev/";
    public const string DirectServerIp = "70.34.201.253";
    public const string DirectApiBaseUrl = "https://70.34.201.253/";
    public const string RemoteServersDiscoveryUrl = "https://raw.githubusercontent.com/OctoCore-Dev/obxodka/main/servers.json";

    public static readonly string[] KnownDomainBases =
    [
        DefaultApiBaseUrl,
        LegacyApiBaseUrl
    ];

    public static string BaseUrl => ApiBaseUrl;

    public static string ApiBaseUrl
    {
        get;
        set => field = string.IsNullOrWhiteSpace(value)
            ? DefaultApiBaseUrl
            : value.Trim();
    } = DefaultApiBaseUrl;

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
