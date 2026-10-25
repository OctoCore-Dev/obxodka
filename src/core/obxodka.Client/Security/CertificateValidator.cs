namespace obxodka.Client.Security;

public static class CertificateValidator
{
    public static bool ValidateServerCertificate(
        X509Certificate? certificate,
        X509Chain? chain = null,
        SslPolicyErrors errors = SslPolicyErrors.None,
        string? dynamicPinningHash = null)
    {
        if (certificate is null)
        {
            return false;
        }

        X509Certificate2? ownedCert = null;
        try
        {
            var cert2 = certificate as X509Certificate2 ?? (ownedCert = new X509Certificate2(certificate));

            var nowUtc = DateTime.UtcNow;
            var notBeforeUtc = cert2.NotBefore.ToUniversalTime();
            var notAfterUtc = cert2.NotAfter.ToUniversalTime();

            if (nowUtc < notBeforeUtc - TimeSpan.FromDays(1) || nowUtc > notAfterUtc)
            {
                return false;
            }

            var expectedPin = dynamicPinningHash is not null
                ? (string.IsNullOrWhiteSpace(dynamicPinningHash) ? null : dynamicPinningHash)
                : OctopusEngine.DynamicSslPublicKeyHash;

            var pubKey = cert2.GetPublicKey();
            var hash = Convert.ToBase64String(SHA256.HashData(pubKey));
            Shared.Logging.AppLogger.Log($"[CERT] Validating server certificate: Subject='{cert2.Subject}', Thumbprint='{cert2.Thumbprint}', PubKeyHash='{hash}', NotAfter='{cert2.NotAfter:u}', SslErrors='{errors}', ExpectedPin='{expectedPin ?? "none"}'");

            if (!string.IsNullOrWhiteSpace(expectedPin))
            {
                if (string.Equals(hash, expectedPin, StringComparison.Ordinal))
                {
                    Shared.Logging.AppLogger.Log($"[CERT OK] Matched dynamic public key hash '{expectedPin}'");
                    return true;
                }

                using var key = cert2.GetRSAPublicKey() as AsymmetricAlgorithm ?? cert2.GetECDsaPublicKey();
                if (key is not null)
                {
                    var spkiHash = Convert.ToBase64String(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
                    if (string.Equals(spkiHash, expectedPin, StringComparison.Ordinal))
                    {
                        Shared.Logging.AppLogger.Log($"[CERT OK] Matched SPKI public key hash '{expectedPin}'");
                        return true;
                    }
                }

                if (string.Equals(cert2.Thumbprint, expectedPin, StringComparison.OrdinalIgnoreCase))
                {
                    Shared.Logging.AppLogger.Log($"[CERT OK] Matched thumbprint '{expectedPin}'");
                    return true;
                }
            }

            foreach (var backupHash in AppSecrets.BackupPublicKeyHashes)
            {
                if (string.Equals(cert2.Thumbprint, backupHash, StringComparison.OrdinalIgnoreCase) || string.Equals(hash, backupHash, StringComparison.Ordinal))
                {
                    Shared.Logging.AppLogger.Log($"[CERT OK] Matched hardcoded backup thumbprint '{backupHash}'");
                    return true;
                }
            }

            if (!string.IsNullOrWhiteSpace(dynamicPinningHash))
            {
                Shared.Logging.AppLogger.LogError($"[CERT PINNING MISMATCH] Expected explicit pin: '{dynamicPinningHash}', Actual PubKeyHash: '{hash}', Thumbprint: '{cert2.Thumbprint}'");
                return false;
            }

            var isTrustedDomain = IsTrustedObxodkaCertificate(cert2);

            if (isTrustedDomain)
            {
                var nonNameErrors = errors & ~SslPolicyErrors.RemoteCertificateNameMismatch;
                if (nonNameErrors == SslPolicyErrors.None)
                {
                    using var verifyChain = new X509Chain();
                    verifyChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    if (verifyChain.Build(cert2))
                    {
                        Shared.Logging.AppLogger.Log("[CERT OK] Trusted domain chain verified successfully.");
                        return true;
                    }

                    if (chain is not null)
                    {
                        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        if (chain.Build(cert2))
                        {
                            Shared.Logging.AppLogger.Log("[CERT OK] Trusted domain passed via outer chain.");
                            return true;
                        }
                    }
                }
            }

            if (errors == SslPolicyErrors.None && string.IsNullOrWhiteSpace(expectedPin))
            {
                using var verifyChain = new X509Chain();
                verifyChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                if (verifyChain.Build(cert2))
                {
                    Shared.Logging.AppLogger.Log("[CERT OK] Default system root CA chain verified.");
                    return true;
                }

                if (chain is not null)
                {
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    if (chain.Build(cert2))
                    {
                        Shared.Logging.AppLogger.Log("[CERT OK] Default outer chain verified.");
                        return true;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(expectedPin))
            {
                Shared.Logging.AppLogger.LogError($"[CERT PINNING MISMATCH] Expected pin: '{expectedPin}', Actual PubKeyHash: '{hash}', Thumbprint: '{cert2.Thumbprint}', SslErrors: '{errors}'");
            }
            else
            {
                Shared.Logging.AppLogger.LogError($"[CERT REJECTED] Server certificate did not match trusted pins or trusted root CA. Errors: '{errors}'");
            }

            return false;
        }
        catch (Exception ex)
        {
            Shared.Logging.AppLogger.LogError($"[CERT ERROR] Exception during certificate validation: {ex.Message}", ex);
            return false;
        }
        finally
        {
            ownedCert?.Dispose();
        }
    }

    private static bool IsTrustedObxodkaCertificate(X509Certificate2 cert2)
    {
        foreach (var ext in cert2.Extensions)
        {
            if (ext is X509SubjectAlternativeNameExtension sanExt)
            {
                foreach (var dns in sanExt.EnumerateDnsNames())
                {
                    if (IsTrustedHostName(dns))
                    {
                        return true;
                    }
                }
            }
        }

        var simpleName = cert2.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        return !string.IsNullOrWhiteSpace(simpleName) && IsTrustedHostName(simpleName.Trim());
    }

    private static bool IsTrustedHostName(string host) =>
        host.Equals(AppConfig.PrimaryDomain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith($".{AppConfig.PrimaryDomain}", StringComparison.OrdinalIgnoreCase) ||
        host.Equals(AppConfig.MirrorDomain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith($".{AppConfig.MirrorDomain}", StringComparison.OrdinalIgnoreCase);
}
