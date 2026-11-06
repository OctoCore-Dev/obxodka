namespace obxodka.Config;

//днс серверы по умолчанию и защита от спуфинга
public static class DnsDefaults
{
    public const string PrimaryDns = "1.1.1.1";
    public const string SecondaryDns = "1.0.0.1";
    public const string PrimaryDnsV6 = "2606:4700:4700::1111";
    public const string SecondaryDnsV6 = "2001:4860:4860::8888";

    //днс серверы для конфигурации vpn туннелей
    public static readonly string[] TunnelDnsServersIPv4 = [PrimaryDns, SecondaryDns, "8.8.8.8"];
    public static readonly string[] TunnelDnsServersIPv6 = [PrimaryDnsV6, SecondaryDnsV6];

    //проверенные публичные днс серверы
    public static readonly string[] TrustedDnsServers =
    [
        "77.88.8.8",
        "77.88.8.1",
        "1.1.1.1",
        "1.0.0.1",
        "8.8.8.8",
        "8.8.4.4",
        "9.9.9.9",
        "149.112.112.112",
        "208.67.222.222",
        "208.67.220.220"
    ];

    //публичные doh серверы для защищенного поиска ip
    public static readonly string[] DohResolvers =
    [
        "https://cloudflare-dns.com/dns-query",
        "https://dns.adguard.com/dns-query",
        "https://1.1.1.1/dns-query",
        "https://8.8.8.8/resolve"
    ];

    //собирает doh ссылку для запроса а записи
    public static string BuildDohQueryUrl(string resolver, string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var separator = resolver.Contains('?') ? '&' : '?';
        return $"{resolver}{separator}name={Uri.EscapeDataString(host)}&type=A";
    }
}
