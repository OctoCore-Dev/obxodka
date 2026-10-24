namespace obxodka.Config;

public static class AppConfig
{
    public const string DefaultApiBaseUrl = "https://obxodka.one/";
    public const string DirectServerIp = "70.34.201.253";
    public const string DirectApiBaseUrl = "https://70.34.201.253/";
    public const string RemoteServersDiscoveryUrl = "https://raw.githubusercontent.com/OctoCore-Dev/obxodka/develop/servers.json";
    public const string RemoteServersDiscoveryFallbackUrl = "https://raw.githubusercontent.com/OctoCore-Dev/obxodka/main/servers.json";

    public static readonly string[] KnownDomainBases =
    [
        DefaultApiBaseUrl,
        DirectApiBaseUrl
    ];

    public static readonly List<VpnServerDto> BootstrapServers =
    [
        new(
            Ip: "obxodka.one",
            Port: 443,
            Location: "Швеция, Стокгольм (Cloudflare)",
            IsOnline: true,
            LoadPercent: 5,
            CertHash: "",
            Provider: "Cloudflare",
            Flag: "SE",
            Role: "shield"
        ),
        new(
            Ip: "70.34.201.253",
            Port: 443,
            Location: "Швеция, Стокгольм",
            IsOnline: true,
            LoadPercent: 10,
            CertHash: "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U=",
            Provider: "Vultr",
            Flag: "SE",
            Role: "main"
        ),
        new(
            Ip: "70.34.246.53",
            Port: 443,
            Location: "Польша, Варшава",
            IsOnline: true,
            LoadPercent: 8,
            CertHash: "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U=",
            Provider: "Vultr",
            Flag: "PL",
            Role: "worker"
        )
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
