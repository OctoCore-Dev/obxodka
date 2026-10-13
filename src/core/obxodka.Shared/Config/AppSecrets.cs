namespace obxodka.Config;

public static class AppSecrets
{
    public static string? SslPublicKeyHash { get; set; }

    public static readonly string[] BackupPublicKeyHashes =
    [
        "CBDEBFDE826F40C6BF0F77525522E624C7A624BC"
    ];

    public static readonly string[] AllowedSniPool =
    [
        "obxodka.one",
        "www.obxodka.one"
    ];

    public static readonly string[] BaseDomains =
    [
        "obxodka.one"
    ];

    public static readonly string[] SubdomainPrefixes =
    [
        "api",
        "auth",
        "cdn",
        "edge",
        "static",
        "sync",
        "core",
        "telemetry",
        "portal",
        "gateway",
        "node",
        "cluster",
        "stream",
        "v133",
        "speed",
        "cloud",
        "proxy",
        "ingress",
        "relay",
        "mesh"
    ];

    public static string GetRandomSni()
    {
        if (Random.Shared.Next(100) < 15)
        {
            return AllowedSniPool[Random.Shared.Next(AllowedSniPool.Length)];
        }

        var domain = BaseDomains[Random.Shared.Next(BaseDomains.Length)];
        var prefix = SubdomainPrefixes[Random.Shared.Next(SubdomainPrefixes.Length)];

        var style = Random.Shared.Next(3);
        return style switch
        {
            0 => $"{prefix}.{domain}",
            1 => $"{prefix}{Random.Shared.Next(1, 10):D2}.{domain}",
            _ => $"{prefix}-{Random.Shared.Next(0x1000, 0xFFFF):x4}.{domain}"
        };
    }

    public static List<string> GetCandidateSniList(int count = 6)
    {
        var result = new List<string>(count)
        {
            "obxodka.one",
            "www.obxodka.one"
        };

        while (result.Count < count)
        {
            var candidate = GetRandomSni();
            if (!result.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    private static readonly byte[] t_obfuscatedSecret =
    [
        0x35, 0xA1, 0x06, 0xCA, 0x3E, 0xA8, 0x1F, 0xFA,
        0x33, 0xAD, 0x0A, 0xC0, 0x28, 0xAD, 0x1F, 0xC9,
        0x05, 0xB3, 0x1F, 0xD6, 0x29
    ];

    private static readonly byte[] t_secretMask = [0x5A, 0xC3, 0x7E, 0xA5];

    public static string InternalPfxPassword => ResolveInternalPassword();
    public static ReadOnlySpan<byte> InternalPfxPasswordUtf8 => Encoding.UTF8.GetBytes(InternalPfxPassword);

    private static string ResolveInternalPassword()
    {
        var envSecret = Environment.GetEnvironmentVariable("OBXODKA_INTERNAL_PFX_PASSWORD");
        if (!string.IsNullOrWhiteSpace(envSecret))
        {
            return envSecret;
        }

        Span<byte> decrypted = stackalloc byte[t_obfuscatedSecret.Length];
        for (var i = 0; i < t_obfuscatedSecret.Length; i++)
        {
            decrypted[i] = (byte)(t_obfuscatedSecret[i] ^ t_secretMask[i % t_secretMask.Length]);
        }

        return Encoding.UTF8.GetString(decrypted);
    }
}

