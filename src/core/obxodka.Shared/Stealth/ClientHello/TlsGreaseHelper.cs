namespace obxodka.Shared.Stealth.ClientHello;

//генератор случайных значений grease для tls
public static class TlsGreaseHelper
{
    private static readonly ushort[] s_greaseValues =
    [
        0x0a0a, 0x1a1a, 0x2a2a, 0x3a3a,
        0x4a4a, 0x5a5a, 0x6a6a, 0x7a7a,
        0x8a8a, 0x9a9a, 0xaaaa, 0xbaba,
        0xcaca, 0xdada, 0xeaea, 0xfafa
    ];

    //случайное 16 битное значение grease
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort GetRandomGrease() =>
        s_greaseValues[RandomNumberGenerator.GetInt32(0, s_greaseValues.Length)];
}
