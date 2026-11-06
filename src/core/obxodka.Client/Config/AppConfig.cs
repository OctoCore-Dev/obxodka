namespace obxodka.Config;

//основная конфигурация клиента базовые адреса домены и пути api
public static class AppConfig
{
    //список встроенных серверов
    public static List<VpnServerDto> BootstrapServers => BootstrapConfig.Servers;

    //основной домен через щит cloudflare
    public static string PrimaryDomain =>
        BootstrapServers.FirstOrDefault(s => string.Equals(s.Role, "shield", StringComparison.OrdinalIgnoreCase))?.Ip ?? "localhost";

    //прямой ip адрес основного сервера
    public static string DirectServerIp =>
        BootstrapServers.FirstOrDefault(s => string.Equals(s.Role, "main", StringComparison.OrdinalIgnoreCase))?.Ip ?? "127.0.0.1";

    //базовый адрес запросов через щит
    public static string DefaultApiBaseUrl => $"https://{PrimaryDomain}/";

    //прямой адрес для аварийных запросов к серверу напрямую
    public static string DirectApiBaseUrl => $"https://{DirectServerIp}/";

    //прямые адреса серверов для обхода блокировок
    public static string[] DirectRelayIps => [DirectServerIp];

    /*
     * на будущее воркер серваки 

    public static string WorkerServerIp =>
        BootstrapServers.FirstOrDefault(s => string.Equals(s.Role, "worker", StringComparison.OrdinalIgnoreCase))?.Ip ?? DirectServerIp;

    public static string WorkerApiBaseUrl => $"https://{WorkerServerIp}/";

    public static string[] DirectRelayIpsWithWorker => WorkerServerIp != DirectServerIp
        ? [DirectServerIp, WorkerServerIp]
        : [DirectServerIp];
    */

    //зеркала и ссылки поиска серверов
    public static string MirrorDomain => MirrorConfig.MirrorDomain;
    public static string[] MirrorDomains => MirrorConfig.MirrorDomains;
    public static string MirrorApiBaseUrl => MirrorConfig.MirrorApiBaseUrl;
    public static string[] RemoteServersDiscoveryUrls => MirrorConfig.RemoteServersDiscoveryUrls;
    public const string RemoteServersDiscoveryUrl = MirrorConfig.RemoteServersDiscoveryUrl;
    public const string RemoteServersDiscoveryFallbackUrl = MirrorConfig.RemoteServersDiscoveryFallbackUrl;
    public static string[] KnownDomainBases => MirrorConfig.KnownDomainBases;

    //текущий рабочий адрес api с возможностью смены на лету
    public static string ApiBaseUrl
    {
        get => field ?? DefaultApiBaseUrl;
        set => field = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    //собирает полный url для api запроса из строки
    public static string ApiUrl(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        return $"{ApiBaseUrl.AsSpan().TrimEnd('/')}/{endpoint.AsSpan().TrimStart('/')}";
    }

    //собирает полный url для api запроса из спана без лишней памяти
    public static string ApiUrl(ReadOnlySpan<char> endpoint)
    {
        return endpoint.IsWhiteSpace()
            ? throw new ArgumentException("Endpoint cannot be empty.", nameof(endpoint))
            : $"{ApiBaseUrl.AsSpan().TrimEnd('/')}/{endpoint.TrimStart('/')}";
    }
}
