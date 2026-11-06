namespace obxodka.Client.Security;

//фабрика настроек tls и ssl аутентификации для сокетов и http клиентов
public static class SslOptionsFactory
{
    //базовые настройки ssl с валидацией сертификата сервера, ssl pinning и выбором набора шифров под профиль браузера
    public static SslClientAuthenticationOptions Create(
        string? targetHost = null,
        string? dynamicPinningHash = null,
        List<SslApplicationProtocol>? applicationProtocols = null,
        TlsBrowserProfile browserProfile = TlsBrowserProfile.Chrome)
    {
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = targetHost,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            EnabledSslProtocols = SslProtocols.None,
            ApplicationProtocols = applicationProtocols,
            RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                CertificateValidator.ValidateServerCertificate(cert, chain, errors, dynamicPinningHash)
        };

        if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
        {
            try
            {
                options.CipherSuitesPolicy = new CipherSuitesPolicy(RoutingHello.GetCipherSuites(browserProfile));
            }
            catch (PlatformNotSupportedException)
            {
            }
        }

        return options;
    }

    //настройки ssl без пиннинга для публичных doh резолверов и внешней диагностики
    public static SslClientAuthenticationOptions CreateStandard(
        string? targetHost = null,
        List<SslApplicationProtocol>? applicationProtocols = null) =>
        new()
        {
            TargetHost = targetHost,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            EnabledSslProtocols = SslProtocols.None,
            ApplicationProtocols = applicationProtocols
        };

    //настройки ssl для doh резолвера
    public static SslClientAuthenticationOptions CreatePublicDoh(
        string? targetHost = null,
        List<SslApplicationProtocol>? applicationProtocols = null) =>
        CreateStandard(targetHost, applicationProtocols);
}
