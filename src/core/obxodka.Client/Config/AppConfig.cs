namespace obxodka.Config;

//основная конфигурация клиента базовые адреса домены и пути api
public static class AppConfig
{
    //секретный токен авторизации телеметрии для защиты шлюза
    public const string TelemetryAuthToken = "obx_telemetry_secure_gate_2026";

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

    //зеркала и ссылки поиска серверов
    public static string MirrorDomain => MirrorConfig.MirrorDomain;
    public static string[] MirrorDomains => MirrorConfig.MirrorDomains;
    public static string MirrorApiBaseUrl => MirrorConfig.MirrorApiBaseUrl;
    public static string[] RemoteServersDiscoveryUrls => MirrorConfig.RemoteServersDiscoveryUrls;
    public static string[] KnownDomainBases => MirrorConfig.KnownDomainBases;

    //текущий рабочий адрес api с возможностью смены на лету
    [field: AllowNull]
    [field: MaybeNull]
    public static string ApiBaseUrl
    {
        get => field ?? DefaultApiBaseUrl;
        set => field = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    //окружение клиента для телеметрии и мониторинга ошибок (testing, production, development)
    public static string EnvironmentName
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("OBXODKA_ENVIRONMENT");
            if (!string.IsNullOrWhiteSpace(env))
            {
                return env.Trim().ToLowerInvariant();
            }

#if DEBUG
            return "development";
#elif TESTING_BUILD
            return "testing";
#else
            return "production";
#endif
        }
    }

    //собирает полный url для api запроса из строки
    public static string ApiUrl(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        return $"{ApiBaseUrl.AsSpan().TrimEnd('/')}/{endpoint.AsSpan().TrimStart('/')}";
    }
}
