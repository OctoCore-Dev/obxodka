namespace obxodka.Config;

//ключи хэши ssl pinning и маскированные пароли
public static class AppSecrets
{
    //резервные хэши ключей для ротации сертификатов
    public static readonly string[] BackupPublicKeyHashes =
    [
        "CBDEBFDE826F40C6BF0F77525522E624C7A624BC",
        "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U="
    ];

    //замаскированный секретный ключ xor от реверса
    private static readonly byte[] s_obfuscatedSecret =
    [
        0x35, 0xA1, 0x06, 0xCA, 0x3E, 0xA8, 0x1F, 0xFA,
        0x33, 0xAD, 0x0A, 0xC0, 0x28, 0xAD, 0x1F, 0xC9,
        0x05, 0xB3, 0x1F, 0xD6, 0x29
    ];

    private static readonly byte[] s_secretMask = [0x5A, 0xC3, 0x7E, 0xA5];

    //пароль для клиентского pfx сертификата
    public static string InternalPfxPassword => ResolveInternalPassword();

    //обфускация адреса в формат obx://
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

    //деобфускация адреса из формата obx://
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
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return endpoint;
        }
    }

    private static string ResolveInternalPassword()
    {
        var envSecret = Environment.GetEnvironmentVariable("OBXODKA_INTERNAL_PFX_PASSWORD");
        if (!string.IsNullOrWhiteSpace(envSecret))
        {
            return envSecret;
        }

        Span<byte> decrypted = stackalloc byte[s_obfuscatedSecret.Length];
        for (var i = 0; i < s_obfuscatedSecret.Length; i++)
        {
            decrypted[i] = (byte)(s_obfuscatedSecret[i] ^ s_secretMask[i % s_secretMask.Length]);
        }

        return Encoding.UTF8.GetString(decrypted);
    }
}
