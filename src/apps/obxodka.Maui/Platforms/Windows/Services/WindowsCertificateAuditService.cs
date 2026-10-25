namespace obxodka.Maui.Platforms.Windows.Services;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsCertificateAuditService : ICertificateAuditService
{
    private static readonly string[] t_suspiciousPatterns =
    [
        "Russian Trusted",
        "Минцифры",
        "Головной удостоверяющий центр",
        "Russian Root CA",
        "National Certification Authority",
        "Министерство цифрового развития"
    ];

    public Task<CertificateAuditResult> CheckCertificatesAsync()
    {
        try
        {
            var userResult = CheckStore(StoreLocation.CurrentUser);
            if (userResult.HasUntrustedRoot)
            {
                return Task.FromResult(userResult);
            }

            var machineResult = CheckStore(StoreLocation.LocalMachine);
            if (machineResult.HasUntrustedRoot)
            {
                return Task.FromResult(machineResult);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowsCertificateAuditService] Check error: {ex.Message}");
        }

        return Task.FromResult(new CertificateAuditResult(false, null, null, null));
    }

    private static CertificateAuditResult CheckStore(StoreLocation location)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, location);
            store.Open(OpenFlags.ReadOnly);

            foreach (var cert in store.Certificates)
            {
                var issuer = cert.Issuer ?? string.Empty;
                var subject = cert.Subject ?? string.Empty;

                foreach (var pattern in t_suspiciousPatterns)
                {
                    if (issuer.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                        subject.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        var name = !string.IsNullOrWhiteSpace(cert.FriendlyName)
                            ? cert.FriendlyName
                            : cert.GetNameInfo(X509NameType.SimpleName, false) ?? "Russian Trusted Root CA";

                        var locName = location == StoreLocation.CurrentUser ? "Хранилище пользователя" : "Хранилище компьютера";
                        var details = $"Найден в '{locName}'. Отпечаток: {cert.Thumbprint}";
                        return new CertificateAuditResult(true, name, cert.Thumbprint, details);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowsCertificateAuditService] Store error ({location}): {ex.Message}");
        }

        return new CertificateAuditResult(false, null, null, null);
    }

    public Task OpenCertificateSettingsAsync()
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = "certmgr.msc",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowsCertificateAuditService] Failed to open certmgr: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public Task<bool> TryRemoveUserCertificateAsync(string thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return Task.FromResult(false);
        }

        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);

            var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
            if (found.Count > 0)
            {
                store.Remove(found[0]);
                return Task.FromResult(true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowsCertificateAuditService] Remove error: {ex.Message}");
        }

        return Task.FromResult(false);
    }
}
