namespace obxodka.Config;

public static class AppSecrets
{
    public static string? SslPublicKeyHash { get; set; }

    public static readonly string[] BackupPublicKeyHashes = [];

    public static readonly string[] AllowedSniPool =
    [
        "google.com",
        "www.google.com",
        "microsoft.com",
        "www.microsoft.com",
        "apple.com",
        "www.apple.com",
        "cloudflare.com",
        "www.cloudflare.com",
        "skype.com",
        "www.skype.com"
    ];

    public static string GetRandomSni() =>
        AllowedSniPool[Random.Shared.Next(AllowedSniPool.Length)];

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

