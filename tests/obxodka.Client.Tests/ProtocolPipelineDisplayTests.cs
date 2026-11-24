namespace obxodka.Client.Tests;

//тесты реестра протоколов, бейджей активного транспорта и сброса состояния при отключении
[Collection(nameof(GlobalStaticStateCollection))]
public sealed class ProtocolPipelineDisplayTests
{
    [Fact]
    public void GetRegisteredFactories_ReturnsAllCoreTransportsInPriorityOrder()
    {
        var factories = ProtocolPipelineRegistry.GetRegisteredFactories();

        Assert.True(factories.Count >= 3);
        Assert.Equal(TransportProtocolKind.ObxodkaStream, factories[0].Kind);
        Assert.Equal(TransportProtocolKind.ObxodkaWebSocket, factories[1].Kind);
        Assert.Equal(TransportProtocolKind.ObxodkaXhttp, factories[2].Kind);
        Assert.All(factories, f =>
        {
            Assert.False(string.IsNullOrWhiteSpace(f.FriendlyName));
            Assert.False(string.IsNullOrWhiteSpace(f.ShortTag));
            Assert.False(string.IsNullOrWhiteSpace(f.TransportSummary));
            Assert.False(string.IsNullOrWhiteSpace(f.Description));
        });
    }

    [Theory]
    [InlineData(null, 2, "Подбор...")]
    [InlineData("", 2, "Подбор...")]
    [InlineData("AUTO", 2, "Подбор...")]
    [InlineData("OBXODKA-STREAM", 3, "Stream • 3L")]
    [InlineData("OBXODKA-STREAM", 2, "Stream • 2L")]
    [InlineData("OBXODKA-STREAM", 0, "Stream • 1L")]
    [InlineData("OBXODKA-WS", 1, "WebSocket")]
    [InlineData("OBXODKA-XHTTP", 1, "XHTTP")]
    public void FormatLiveStatusBadge_FormatsExpectedProtocolTags(string? protocolName, int rays, string expected)
    {
        var badge = ProtocolPipelineRegistry.FormatLiveStatusBadge(protocolName, rays);
        Assert.Equal(expected, badge);
    }

    [Fact]
    public async Task OctopusEngine_DisposeAsync_ResetsActiveProtocolAndLocation()
    {
        var engine = OctopusEngine.Current;
        engine.SetActiveServerLocation("Швеция, Стокгольм", "🇸🇪");
        Assert.Equal("Швеция, Стокгольм", engine.ActiveServerLocation);

        await engine.DisposeAsync();

        Assert.Equal("AUTO", engine.ActiveProtocol);
        Assert.Null(engine.ActiveServerLocation);
        Assert.Null(engine.ActiveServerFlag);
    }
}
