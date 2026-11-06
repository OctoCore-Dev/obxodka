namespace obxodka.Stealth;

//генерация структур tls clienthello
public static class ClientHelloBuilder
{
    //генерация chrome clienthello
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] BuildChrome120ClientHello(
        ReadOnlySpan<byte> sni,
        ReadOnlySpan<byte> sessionId = default,
        ReadOnlySpan<byte> pskBinder = default)
    {
        _ = pskBinder;
        return RoutingHello.BuildHello(TlsBrowserProfile.Chrome, sni, sessionId);
    }

    //генерация clienthello под выбранный браузер
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] Build(
        TlsBrowserProfile profile,
        ReadOnlySpan<byte> sni,
        ReadOnlySpan<byte> sessionId = default) =>
        RoutingHello.BuildHello(profile, sni, sessionId);

    //выбор clienthello по шагу ротации
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] GetNext(
        ReadOnlySpan<byte> sni,
        int attemptIndex,
        out TlsBrowserProfile usedProfile) =>
        RoutingHello.GetNextHello(sni, attemptIndex, out usedProfile);
}
