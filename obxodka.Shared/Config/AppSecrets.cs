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

    public const string InternalPfxPassword = "obxodka_internal_pass";
    public static ReadOnlySpan<byte> InternalPfxPasswordUtf8 => "obxodka_internal_pass"u8;
}

