namespace obxodka.Platforms.Windows;

//аудит системных хранилищ сертификатов windows на наличие недоверенных корневых центров минцифры
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsCertificateAuditService : ICertificateAuditService
{
    private static readonly string[] s_suspiciousPatterns =
    [
        "Russian Trusted",
        "Минцифры",
        "Головной удостоверяющий центр",
        "Russian Root CA",
        "National Certification Authority",
        "Министерство цифрового развития"
    ];

    private static readonly HashSet<string> s_knownRussianRootThumbprints = new(
    [
        "4B4791C5364860B73C06D3381FA2CC9EE330DC3E",
        "71EA6F4F24B5CA91D3EC5BE867E411F589255EC3",
        "934E62A4D048E71887ADAB0F622D109F0868F001"
    ], StringComparer.OrdinalIgnoreCase);

    //проверка хранилищ текущего пользователя и локального компьютера
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
            AppLogger.LogError($"[CERT AUDIT ERROR] Check failed: {ex.Message}");
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
                try
                {
                    var thumbprint = cert.Thumbprint ?? string.Empty;
                    var issuer = cert.Issuer ?? string.Empty;
                    var subject = cert.Subject ?? string.Empty;

                    var isKnownThumbprint = s_knownRussianRootThumbprints.Contains(thumbprint);
                    var matchesPattern = false;

                    if (!isKnownThumbprint)
                    {
                        foreach (var pattern in s_suspiciousPatterns)
                        {
                            if (issuer.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                                subject.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                            {
                                matchesPattern = true;
                                break;
                            }
                        }
                    }

                    if (isKnownThumbprint || matchesPattern)
                    {
                        var name = !string.IsNullOrWhiteSpace(cert.FriendlyName)
                            ? cert.FriendlyName
                            : cert.GetNameInfo(X509NameType.SimpleName, false) ?? "Russian Trusted Root CA";

                        var locName = location == StoreLocation.CurrentUser ? "Хранилище пользователя" : "Хранилище компьютера";
                        var details = $"Найден в '{locName}'. Отпечаток: {thumbprint}";
                        return new CertificateAuditResult(true, name, thumbprint, details);
                    }
                }
                finally
                {
                    cert.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[CERT AUDIT ERROR] Store error ({location}): {ex.Message}");
        }

        return new CertificateAuditResult(false, null, null, null);
    }

    //открытие системной оснастки управления сертификатами
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
            AppLogger.LogError($"[CERT AUDIT ERROR] Failed to open certmgr: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    //попытка удаления сертификата из пользовательского хранилища
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
            try
            {
                if (found.Count > 0)
                {
                    store.Remove(found[0]);
                    return Task.FromResult(true);
                }
            }
            finally
            {
                foreach (var cert in found)
                {
                    cert.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[CERT AUDIT ERROR] Remove error: {ex.Message}");
        }

        return Task.FromResult(false);
    }
}
