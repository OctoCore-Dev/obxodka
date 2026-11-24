namespace obxodka.Client.Tests;

[Collection("GlobalStaticState")]
public class WebSocketTransportTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ObxodkaWebSocketTransportInitializesWithCorrectProtocolNameAndDefaults()
    {
        using var transport = new ObxodkaWebSocketTransport(443, AppConfig.PrimaryDomain);
        Assert.Equal("OBXODKA-WS", transport.ProtocolName);
        Assert.Equal(443, transport.EffectivePort);
        Assert.Equal(AppConfig.PrimaryDomain, transport.ActiveSni);
        Assert.False(transport.IsConnected);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task ObxodkaWebSocketTransportCanHandshakeWithLiveServerAsync()
    {
        if (Environment.GetEnvironmentVariable("OBXODKA_RUN_LIVE_TESTS") != "1")
        {
            return;
        }

        using var transport = new ObxodkaWebSocketTransport(443, AppConfig.PrimaryDomain);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var dummyThumbprint = "0123456789ABCDEF0123456789ABCDEF";

        var (ip, ip6) = await transport.ConnectAsync(AppConfig.PrimaryDomain, dummyThumbprint, cts.Token);
        Assert.NotNull(ip);
        Assert.True(IPAddress.TryParse(ip, out _));
        Assert.True(string.IsNullOrEmpty(ip6) || IPAddress.TryParse(ip6, out _));
        Assert.True(transport.IsConnected);

        await transport.SendDisconnectSignalAsync();
    }
}
