namespace obxodka.Config;

public static class AppSecrets
{
    public static string? SslPublicKeyHash { get; set; }

    public static readonly string[] BackupPublicKeyHashes =
    [
        "CBDEBFDE826F40C6BF0F77525522E624C7A624BC",
        "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U="
    ];

    public static readonly string[] StealthTunnelPaths =
    [
        "/api/v1/sync",
        "/api/v1/stream",
        "/cdn-cgi/rum",
        "/v1/telemetry",
        "/g/collect",
        "/api/v1/ws"
    ];

    public static readonly string[] AllowedSniPool =
    [
        "login.live.com",
        "update.code.visualstudio.com",
        "teams.microsoft.com",
        "client.wns.windows.com",
        "dl.google.com",
        "c.s-microsoft.com",
        "www.microsoft.com",
        "fonts.googleapis.com",
        "login.microsoftonline.com",
        "ajax.googleapis.com"
    ];

    public static readonly string[] BaseDomains =
    [
        "gstatic.com",
        "googleapis.com",
        "jsdelivr.net"
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

    public static string GetRandomSni() =>
        AllowedSniPool[Random.Shared.Next(AllowedSniPool.Length)];

    public static List<string> GetCandidateSniList(int count = 8)
    {
        var result = new List<string>(count);
        foreach (var sni in AllowedSniPool)
        {
            if (result.Count < count && !result.Contains(sni, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(sni);
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

    public static string EncodeObfuscatedEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.StartsWith("obx://", StringComparison.OrdinalIgnoreCase))
        {
            return endpoint;
        }

        var raw = Encoding.UTF8.GetBytes(endpoint.Trim());
        var key = Encoding.UTF8.GetBytes(InternalPfxPassword);
        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] ^= (byte)(key[i % key.Length] ^ (i * 31));
        }

        return $"obx://{Convert.ToBase64String(raw)}";
    }

    public static string DecodeObfuscatedEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || !endpoint.StartsWith("obx://", StringComparison.OrdinalIgnoreCase))
        {
            return endpoint;
        }

        try
        {
            var raw = Convert.FromBase64String(endpoint[6..].Trim());
            var key = Encoding.UTF8.GetBytes(InternalPfxPassword);
            for (var i = 0; i < raw.Length; i++)
            {
                raw[i] ^= (byte)(key[i % key.Length] ^ (i * 31));
            }

            return Encoding.UTF8.GetString(raw);
        }
        catch
        {
            return endpoint;
        }
    }

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

    public static string GenerateStealthSignature(string endpoint, out string timestamp)
    {
        timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var keyBytes = Encoding.UTF8.GetBytes(InternalPfxPassword);
        var payloadBytes = Encoding.UTF8.GetBytes($"{timestamp}:{endpoint}");
        Span<byte> hash = stackalloc byte[32];
        _ = HMACSHA256.HashData(keyBytes, payloadBytes, hash);
        return Convert.ToHexStringLower(hash[..12]);
    }
}
