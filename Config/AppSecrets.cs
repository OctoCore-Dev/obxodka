namespace obxodka.Config;

public static class AppSecrets
{
    public const string SslPublicKeyHash = "7WPSq6roPslDBvIe+D131Nb90frQAze3v958opl/XHk=";
    public const string InternalPfxPassword = "obxodka_internal_pass";
    public static ReadOnlySpan<byte> InternalPfxPasswordUtf8 => "obxodka_internal_pass"u8;
}
