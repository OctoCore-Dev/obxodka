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

        try
        {
            using var cert2 = certificate as X509Certificate2 ?? new X509Certificate2(certificate);

            var nowUtc = DateTime.UtcNow;
            var notBeforeUtc = cert2.NotBefore.ToUniversalTime();
            var notAfterUtc = cert2.NotAfter.ToUniversalTime();

            if (nowUtc < notBeforeUtc - TimeSpan.FromDays(1) || nowUtc > notAfterUtc)
            {
                return false;
            }

            var expectedPin = !string.IsNullOrWhiteSpace(dynamicPinningHash)
                ? dynamicPinningHash
                : OctopusEngine.DynamicSslPublicKeyHash;

            if (!string.IsNullOrWhiteSpace(expectedPin))
            {
                var pubKey = cert2.GetPublicKey();
                var hash = Convert.ToBase64String(SHA256.HashData(pubKey));
                if (string.Equals(hash, expectedPin, StringComparison.Ordinal))
                {
                    return true;
                }

                var key = cert2.GetRSAPublicKey() as AsymmetricAlgorithm ?? cert2.GetECDsaPublicKey();
                if (key is not null)
                {
                    var spkiHash = Convert.ToBase64String(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
                    if (string.Equals(spkiHash, expectedPin, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                if (string.Equals(cert2.Thumbprint, expectedPin, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            foreach (var backupHash in AppSecrets.BackupPublicKeyHashes)
            {
                if (string.Equals(cert2.Thumbprint, backupHash, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            var isTrustedDomain = cert2.Subject.Contains("obxodka.one", StringComparison.OrdinalIgnoreCase) ||
                                  cert2.Subject.Contains("octocore.dev", StringComparison.OrdinalIgnoreCase);
            if (!isTrustedDomain)
            {
                foreach (var ext in cert2.Extensions)
                {
                    if (ext is X509SubjectAlternativeNameExtension sanExt)
                    {
                        foreach (var dns in sanExt.EnumerateDnsNames())
                        {
                            if (dns.Equals("obxodka.one", StringComparison.OrdinalIgnoreCase) ||
                                dns.EndsWith(".obxodka.one", StringComparison.OrdinalIgnoreCase) ||
                                dns.Equals("octocore.dev", StringComparison.OrdinalIgnoreCase) ||
                                dns.EndsWith(".octocore.dev", StringComparison.OrdinalIgnoreCase))
                            {
                                isTrustedDomain = true;
                                break;
                            }
                        }
                    }
                    if (isTrustedDomain)
                    {
                        break;
                    }
                }
            }

            if (isTrustedDomain)
            {
                var nonNameErrors = errors & ~SslPolicyErrors.RemoteCertificateNameMismatch;
                if (nonNameErrors == SslPolicyErrors.None)
                {
                    using var verifyChain = new X509Chain();
                    verifyChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    if (verifyChain.Build(cert2))
                    {
                        return true;
                    }

                    if (chain is not null)
                    {
                        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        if (chain.Build(cert2))
                        {
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
                    return true;
                }

                if (chain is not null)
                {
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    if (chain.Build(cert2))
                    {
                        return true;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(expectedPin))
            {
                var pubKey = cert2.GetPublicKey();
                var hash = Convert.ToBase64String(SHA256.HashData(pubKey));
                Debug.WriteLine($"[CERT PINNING MISMATCH] Expected: {expectedPin}, Actual: {hash}, Errors: {errors}");
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
