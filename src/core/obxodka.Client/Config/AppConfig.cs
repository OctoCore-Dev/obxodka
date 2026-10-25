namespace obxodka.Config;

public static class AppConfig
{
    private static readonly (List<VpnServerDto> Servers, string[] Mirrors) t_bootstrapData = LoadBootstrapServersFromJson();
    public static readonly List<VpnServerDto> BootstrapServers = t_bootstrapData.Servers;
    public static readonly string PrimaryDomain = BootstrapServers.FirstOrDefault(s => string.Equals(s.Role, "shield", StringComparison.OrdinalIgnoreCase))?.Ip ?? "localhost";
    public static readonly string[] MirrorDomains = t_bootstrapData.Mirrors;
    public static readonly string MirrorDomain = t_bootstrapData.Mirrors.Length > 1 ? t_bootstrapData.Mirrors[1] : PrimaryDomain;
    public static readonly string DefaultApiBaseUrl = $"https://{PrimaryDomain}/";
    public static readonly string MirrorApiBaseUrl = $"https://{MirrorDomain}/";
    public static readonly string DirectServerIp = BootstrapServers.FirstOrDefault(s => string.Equals(s.Role, "main", StringComparison.OrdinalIgnoreCase))?.Ip ?? "127.0.0.1";
    public static readonly string WorkerServerIp = BootstrapServers.FirstOrDefault(s => string.Equals(s.Role, "worker", StringComparison.OrdinalIgnoreCase))?.Ip ?? DirectServerIp;
    public static readonly string DirectApiBaseUrl = $"https://{DirectServerIp}/";
    public static readonly string WorkerApiBaseUrl = $"https://{WorkerServerIp}/";
    public const string RemoteServersDiscoveryUrl = "https://raw.githubusercontent.com/OctoCore-Dev/obxodka/develop/servers.json";
    public const string RemoteServersDiscoveryFallbackUrl = "https://raw.githubusercontent.com/OctoCore-Dev/obxodka/main/servers.json";

    public static readonly string[] RemoteServersDiscoveryUrls =
    [
        "https://cdn.jsdelivr.net/gh/OctoCore-Dev/obxodka@develop/servers.json",
        RemoteServersDiscoveryUrl,
        "https://cdn.jsdelivr.net/gh/OctoCore-Dev/obxodka@main/servers.json",
        RemoteServersDiscoveryFallbackUrl
    ];

    public static readonly string[] DirectRelayIps =
    [
        DirectServerIp,
        WorkerServerIp
    ];

    public static readonly string[] KnownDomainBases = BuildKnownDomainBases(t_bootstrapData.Mirrors, DirectApiBaseUrl, WorkerApiBaseUrl);

    private static (List<VpnServerDto> Servers, string[] Mirrors) LoadBootstrapServersFromJson()
    {
        try
        {
            string? json = null;
            var localPath = Path.Combine(AppContext.BaseDirectory, "servers.json");
            if (File.Exists(localPath))
            {
                json = File.ReadAllText(localPath);
            }
            else
            {
                using var stream = typeof(AppConfig).Assembly.GetManifestResourceStream("obxodka.servers.json");
                if (stream is not null)
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    json = reader.ReadToEnd();
                }
            }

            if (!string.IsNullOrWhiteSpace(json))
            {
                var parsed = JsonSerializer.Deserialize(json, AppJsonContext.Default.ListVpnServerDto);
                if (parsed is { Count: > 0 })
                {
                    List<string> mirrorList = [];
                    for (var i = 0; i < parsed.Count; i++)
                    {
                        var item = parsed[i];
                        var decIp = AppSecrets.DecodeObfuscatedEndpoint(item.Ip);
                        string[]? decItemMirrors = null;
                        if (item.Mirrors is { Length: > 0 })
                        {
                            decItemMirrors = new string[item.Mirrors.Length];
                            for (var m = 0; m < item.Mirrors.Length; m++)
                            {
                                var dm = AppSecrets.DecodeObfuscatedEndpoint(item.Mirrors[m]);
                                decItemMirrors[m] = dm;
                                if (!mirrorList.Contains(dm, StringComparer.OrdinalIgnoreCase))
                                {
                                    mirrorList.Add(dm);
                                }
                            }
                        }

                        parsed[i] = item with { Ip = decIp, Mirrors = decItemMirrors };
                    }

                    return (parsed, [.. mirrorList]);
                }
            }
        }
        catch
        {
        }

        return ([], []);
    }

    private static string[] BuildKnownDomainBases(string[] mirrors, string directUrl, string workerUrl)
    {
        List<string> urls = new(mirrors.Length + 2);
        foreach (var m in mirrors)
        {
            urls.Add($"https://{m.TrimEnd('/')}/");
        }
        if (urls.Count == 0)
        {
            urls.Add(DefaultApiBaseUrl);
        }
        urls.Add(directUrl);
        urls.Add(workerUrl);
        return [.. urls];
    }

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
