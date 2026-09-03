using Android.Content;
using Android.Provider;
using Java.Security;
using Javax.Net.Ssl;

namespace obxodka.Maui.Platforms.Android.Services;

public sealed class AndroidCertificateAuditService(Context context) : ICertificateAuditService
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
            var keyStore = KeyStore.GetInstance("AndroidCAStore");
            if (keyStore is not null)
            {
                keyStore.Load(null, null);
                var aliases = keyStore.Aliases();
                while (aliases?.HasMoreElements == true)
                {
                    var alias = aliases.NextElement()?.ToString();
                    if (alias is null)
                    {
                        continue;
                    }

                    var cert = keyStore.GetCertificate(alias);
                    if (cert is Java.Security.Cert.X509Certificate x509)
                    {
                        var issuer = x509.IssuerDN?.Name ?? string.Empty;
                        var subject = x509.SubjectDN?.Name ?? string.Empty;

                        foreach (var pattern in t_suspiciousPatterns)
                        {
                            if (issuer.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                                subject.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                            {
                                var name = ExtractCommonName(subject) ?? "Russian Trusted Root CA";
                                var isUser = alias.StartsWith("user:", StringComparison.OrdinalIgnoreCase);
                                var details = isUser
                                    ? "Установлен в раздел 'Пользовательские сертификаты'"
                                    : "Обнаружен в системном хранилище Android";

                                return Task.FromResult(new CertificateAuditResult(true, name, alias, details));
                            }
                        }
                    }
                }
            }

            var tmf = TrustManagerFactory.GetInstance(TrustManagerFactory.DefaultAlgorithm);
            tmf?.Init((KeyStore?)null);
            var trustManagers = tmf?.GetTrustManagers();
            if (trustManagers is not null)
            {
                foreach (var tm in trustManagers)
                {
                    if (tm is IX509TrustManager x509Tm)
                    {
                        var accepted = x509Tm.GetAcceptedIssuers();
                        if (accepted is null)
                        {
                            continue;
                        }

                        foreach (var cert in accepted)
                        {
                            var issuer = cert.IssuerDN?.Name ?? string.Empty;
                            var subject = cert.SubjectDN?.Name ?? string.Empty;

                            foreach (var pattern in t_suspiciousPatterns)
                            {
                                if (issuer.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                                    subject.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                                {
                                    var name = ExtractCommonName(subject) ?? "Russian Trusted Root CA";
                                    return Task.FromResult(new CertificateAuditResult(true, name, null, "Обнаружен в доверенных центрах"));
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AndroidCertificateAuditService] Check error: {ex}");
        }

        return Task.FromResult(new CertificateAuditResult(false, null, null, null));
    }

    private static string? ExtractCommonName(string dn)
    {
        var parts = dn.Split(',');
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[3..];
            }
        }
        return null;
    }

    public Task OpenCertificateSettingsAsync()
    {
        try
        {
            var intent = new Intent("android.settings.CREDENTIAL_STORAGE");
            _ = intent.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
        }
        catch
        {
            try
            {
                var fallbackIntent = new Intent(Settings.ActionSecuritySettings);
                _ = fallbackIntent.AddFlags(ActivityFlags.NewTask);
                context.StartActivity(fallbackIntent);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AndroidCertificateAuditService] Failed to open settings: {ex}");
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> TryRemoveUserCertificateAsync(string thumbprint) => Task.FromResult(false);
}
