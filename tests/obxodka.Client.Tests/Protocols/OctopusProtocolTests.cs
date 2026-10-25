namespace obxodka.Client.Tests;

[Trait("Category", "Unit")]
public class OctopusProtocolTests
{
    [Fact]
    public async Task PumpTrafficAsyncCopiesDataCorrectlyAsync()
    {
        var inputData = "Hello, VPN traffic!";
        var inputBytes = Encoding.UTF8.GetBytes(inputData);
        using var inputStream = new MemoryStream(inputBytes);
        using var outputStream = new MemoryStream();

        using var cts = new CancellationTokenSource();

        _ = await OctopusProtocol.PumpTrafficAsync(inputStream, outputStream, cts.Token);

        var resultData = Encoding.UTF8.GetString(outputStream.ToArray());
        Assert.Equal(inputData, resultData);
    }

    [Fact]
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
        using var transport = new ObxodkaWebSocketTransport(443, AppConfig.PrimaryDomain);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var dummyThumbprint = "0123456789ABCDEF0123456789ABCDEF";

        var (ip, ip6) = await transport.ConnectAsync(AppConfig.PrimaryDomain, dummyThumbprint, cts.Token);
        Assert.NotNull(ip);
        Assert.True(IPAddress.TryParse(ip, out _));
        Assert.True(transport.IsConnected);

        await transport.SendDisconnectSignalAsync();
    }
}
