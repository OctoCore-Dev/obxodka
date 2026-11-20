namespace obxodka.Config;

//домены зеркала ссылки поиска серверов и белый список ssrf
public static class MirrorConfig
{
    //основная ссылка на список серверов в гитхабе
    private const string RemoteServersDiscoveryUrl = "https://raw.githubusercontent.com/OctoCore-Dev/obxodka/main/servers.json";

    //резервная ссылка через cdn на случай блокировки
    private const string RemoteServersDiscoveryFallbackUrl = "https://cdn.jsdelivr.net/gh/OctoCore-Dev/obxodka@main/servers.json";

    //пул зеркал для поиска серверов
    public static readonly string[] RemoteServersDiscoveryUrls =
    [
        RemoteServersDiscoveryFallbackUrl,
        RemoteServersDiscoveryUrl,
        "https://fastly.jsdelivr.net/gh/OctoCore-Dev/obxodka@main/servers.json",
        "https://gcore.jsdelivr.net/gh/OctoCore-Dev/obxodka@main/servers.json"
    ];

    //белый список доверенных адресов для защиты от ssrf
    private static readonly Lazy<string[]> s_knownDomainBases = new(BuildKnownDomainBases);

    //список доменов зеркал из конфига
    public static string[] MirrorDomains => BootstrapConfig.Mirrors;

    //первое рабочее зеркало
    public static string MirrorDomain =>
        BootstrapConfig.Mirrors.Length > 0 ? BootstrapConfig.Mirrors[0] : AppConfig.PrimaryDomain;

    //базовый адрес первого зеркала
    public static string MirrorApiBaseUrl => $"https://{MirrorDomain}/";

    public static string[] KnownDomainBases => s_knownDomainBases.Value;

    private static string[] BuildKnownDomainBases()
    {
        var mirrors = BootstrapConfig.Mirrors;
        var directUrl = AppConfig.DirectApiBaseUrl;

        List<string> urls =
        [
            with(mirrors.Length + 2),
            .. mirrors.Select(m => $"https://{m.TrimEnd('/')}/")
        ];

        if (urls.Count == 0)
        {
            urls.Add(AppConfig.DefaultApiBaseUrl);
        }

        if (!urls.Contains(directUrl, StringComparer.OrdinalIgnoreCase))
        {
            urls.Add(directUrl);
        }

        /*
        //при повторном включении worker добавить его url в белый список
        if (!string.Equals(AppConfig.WorkerApiBaseUrl, directUrl, StringComparison.OrdinalIgnoreCase) && !urls.Contains(AppConfig.WorkerApiBaseUrl, StringComparer.OrdinalIgnoreCase))
        {
            urls.Add(AppConfig.WorkerApiBaseUrl);
        }
        */

        return [.. urls];
    }
}
