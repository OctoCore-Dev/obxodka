namespace obxodka.Client.Security;

//валидация ssl и tls сертификатов сервера и защита от mitm атак
public static class CertificateValidator
{
    //проверка сертификата сервера валидация срока действия открытого ключа и ssl pinning
    public static bool ValidateServerCertificate(
        X509Certificate? certificate,
        X509Chain? chain = null,
        SslPolicyErrors errors = SslPolicyErrors.None,
        string? dynamicPinningHash = null,
        string? expectedHost = null)
    {
        if (certificate is null)
        {
            return false;
        }

        try
        {
            if (certificate is X509Certificate2 cert2)
            {
                return ValidateServerCertificateCore(cert2, chain, errors, dynamicPinningHash, expectedHost);
            }

            using var ownedCert = new X509Certificate2(certificate);
            return ValidateServerCertificateCore(ownedCert, chain, errors, dynamicPinningHash, expectedHost);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException or NotSupportedException
                                       or ObjectDisposedException or InvalidOperationException)
        {
            AppLogger.LogError($"[CERT ERROR] Exception during certificate validation: {ex.Message}", ex);
            return false;
        }
    }

    //внутренняя логика проверки сертификата и цепочки доверия
    private static bool ValidateServerCertificateCore(
        X509Certificate2 cert2,
        X509Chain? chain,
        SslPolicyErrors errors,
        string? dynamicPinningHash,
        string? expectedHost)
    {
        //проверка срока действия сертификата с запасом 1 день на рассинхронизацию часов
        var nowUtc = DateTime.UtcNow;
        var notBeforeUtc = cert2.NotBefore.ToUniversalTime();
        var notAfterUtc = cert2.NotAfter.ToUniversalTime();

        if (nowUtc < notBeforeUtc - TimeSpan.FromDays(1) || nowUtc > notAfterUtc)
        {
            return false;
        }

        //ожидаемый хэш открытого ключа
        var expectedPin = dynamicPinningHash is not null
            ? string.IsNullOrWhiteSpace(dynamicPinningHash) ? null : dynamicPinningHash
            : OctopusEngine.DynamicSslPublicKeyHash;

        var pubKey = cert2.GetPublicKey();
        var hash = Convert.ToBase64String(SHA256.HashData(pubKey));
        string? spkiHash = null;
        using (var key = cert2.GetRSAPublicKey() as AsymmetricAlgorithm ?? cert2.GetECDsaPublicKey())
        {
            if (key is not null)
            {
                spkiHash = Convert.ToBase64String(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
            }
        }

        AppLogger.Log(
            $"[CERT] Validating server certificate: Subject='{cert2.Subject}', PubKeyHash='{hash}', SpkiHash='{spkiHash ?? "none"}', NotAfter='{notAfterUtc:u}', SslErrors='{errors}', ExpectedPin='{expectedPin ?? "none"}'");

        //сверка с ожидаемым sha-256 пином (только публичный ключ или spki, без sha-1)
        if (!string.IsNullOrWhiteSpace(expectedPin))
        {
            if (string.Equals(hash, expectedPin, StringComparison.Ordinal))
            {
                AppLogger.Log($"[CERT OK] Matched dynamic public key hash '{expectedPin}'");
                return true;
            }

            if (spkiHash is not null && string.Equals(spkiHash, expectedPin, StringComparison.Ordinal))
            {
                AppLogger.Log($"[CERT OK] Matched SPKI public key hash '{expectedPin}'");
                return true;
            }
        }

        //сверка с резервными зашитыми sha-256 ключами
        foreach (var backupHash in AppSecrets.BackupPublicKeyHashes)
        {
            if (!string.Equals(hash, backupHash, StringComparison.Ordinal) &&
                (spkiHash is null || !string.Equals(spkiHash, backupHash, StringComparison.Ordinal)))
            {
                continue;
            }

            AppLogger.Log($"[CERT OK] Matched hardcoded backup SHA-256 pin '{backupHash}'");
            return true;
        }

        //если пиннинг был включен но ни один ключ не совпал отклоняем соединение до проверки доменного корня
        if (!string.IsNullOrWhiteSpace(expectedPin))
        {
            AppLogger.LogError(
                $"[CERT PINNING MISMATCH] Expected pin: '{expectedPin}', Actual PubKeyHash: '{hash}', Thumbprint: '{cert2.Thumbprint}', SslErrors: '{errors}'");
            return false;
        }

        //проверка принадлежности сертификата доверенным доменам обходки
        var isTrustedDomain = IsTrustedObxodkaCertificate(cert2);
        var allowNameMismatch = string.IsNullOrWhiteSpace(expectedHost) ||
                                IPAddress.TryParse(expectedHost, out _) ||
                                IsTrustedHostName(expectedHost);

        if (isTrustedDomain && (allowNameMismatch || !errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)))
        {
            var nonNameErrors = errors & ~SslPolicyErrors.RemoteCertificateNameMismatch;
            if (nonNameErrors is SslPolicyErrors.None or SslPolicyErrors.RemoteCertificateChainErrors)
            {
                using var verifyChain = new X509Chain();
                verifyChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                verifyChain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeNested;
                PopulateExtraStore(verifyChain, chain);

                if (verifyChain.Build(cert2) || IsOnlyNonFatalRevocationStatus(verifyChain))
                {
                    AppLogger.Log("[CERT OK] Trusted domain chain verified successfully.");
                    return true;
                }

                if (chain is not null)
                {
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeNested;
                    if (chain.Build(cert2) || IsOnlyNonFatalRevocationStatus(chain))
                    {
                        AppLogger.Log("[CERT OK] Trusted domain passed via outer chain.");
                        return true;
                    }
                }
            }
        }

        //базовая проверка системного корневого центра сертификации для внешних запросов
        if (errors == SslPolicyErrors.None)
        {
            using var verifyChain = new X509Chain();
            verifyChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            PopulateExtraStore(verifyChain, chain);
            if (verifyChain.Build(cert2) || IsOnlyNonFatalRevocationStatus(verifyChain))
            {
                AppLogger.Log("[CERT OK] Default system root CA chain verified.");
                return true;
            }

            if (chain is not null)
            {
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                if (chain.Build(cert2) || IsOnlyNonFatalRevocationStatus(chain))
                {
                    AppLogger.Log("[CERT OK] Default outer chain verified.");
                    return true;
                }
            }
        }

        var chainFlags = chain is { ChainStatus.Length: > 0 }
            ? string.Join(",", chain.ChainStatus.Select(s => s.Status))
            : "none";
        AppLogger.LogError(
            $"[CERT REJECTED] Server certificate did not match trusted pins or trusted root CA. Errors: '{errors}', ChainStatus: '{chainFlags}', Subject: '{cert2.Subject}', Issuer: '{cert2.Issuer}', PubKeyHash: '{hash}', TrustedDomain: {isTrustedDomain}");
        return false;
    }

    //перенос промежуточных сертификатов из элементов цепочки и extrastore исходного рукопожатия
    internal static void PopulateExtraStore(X509Chain target, X509Chain? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var elem in source.ChainElements)
        {
            _ = target.ChainPolicy.ExtraStore.Add(elem.Certificate);
        }

        foreach (var extra in source.ChainPolicy.ExtraStore)
        {
            _ = target.ChainPolicy.ExtraStore.Add(extra);
        }
    }

    //проверка что цепочка доверия не содержит критических ошибок кроме недоступности ocsp/crl
    private static bool IsOnlyNonFatalRevocationStatus(X509Chain chain) =>
        chain.ChainElements.Count > 1 &&
        chain.ChainStatus.Length > 0 &&
        chain.ChainStatus.All(s => s.Status is X509ChainStatusFlags.NoError
            or X509ChainStatusFlags.RevocationStatusUnknown
            or X509ChainStatusFlags.OfflineRevocation);

    //проверка наличия доменов обходки в san сертификата
    internal static bool IsTrustedObxodkaCertificate(X509Certificate2 cert2)
    {
        if (cert2.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(sanExt => sanExt.EnumerateDnsNames())
            .Any(IsTrustedCertificateName))
        {
            return true;
        }

        var simpleName = cert2.GetNameInfo(X509NameType.SimpleName, false);
        return !string.IsNullOrWhiteSpace(simpleName) && IsTrustedCertificateName(simpleName.Trim());
    }

    //проверка имени из сертификата включая wildcard-записи вида *.octocore.dev для поддоменов зеркал
    private static bool IsTrustedCertificateName(string certDnsName)
    {
        var trimmed = certDnsName.Trim();
        if (IsTrustedHostName(trimmed))
        {
            return true;
        }

        if (!trimmed.StartsWith("*.", StringComparison.Ordinal) || trimmed.Length <= 2)
        {
            return false;
        }

        var suffix = trimmed.AsSpan(1);
        if (MatchesWildcardSuffix(AppConfig.PrimaryDomain, suffix) ||
            MatchesWildcardSuffix(AppConfig.MirrorDomain, suffix))
        {
            return true;
        }

        foreach (var mirror in BootstrapConfig.Mirrors)
        {
            if (MatchesWildcardSuffix(mirror, suffix))
            {
                return true;
            }
        }

        return false;
    }

    //проверка одноуровневого wildcard-совпадения по rfc 6125
    private static bool MatchesWildcardSuffix(string? host, ReadOnlySpan<char> wildcardSuffix)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var hostSpan = host.AsSpan().Trim();
        if (hostSpan.Length <= wildcardSuffix.Length || !hostSpan.EndsWith(wildcardSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var prefix = hostSpan[..^wildcardSuffix.Length];
        return prefix.Length > 0 && !prefix.Contains('.');
    }

    //проверка совпадения хоста с основным или зеркальным доменом
    private static bool IsTrustedHostName(string host)
    {
        var trimmed = host.Trim();
        return HostMatchesDomain(trimmed, AppConfig.PrimaryDomain) ||
               HostMatchesDomain(trimmed, AppConfig.MirrorDomain) ||
               BootstrapConfig.Mirrors.Any(m => HostMatchesDomain(trimmed, m));
    }

    //проверка точного совпадения хоста или его поддомена с целевым доменом
    private static bool HostMatchesDomain(string host, string? domain) =>
        !string.IsNullOrWhiteSpace(domain) &&
        (host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
         host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase));
}
