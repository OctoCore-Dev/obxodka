namespace obxodka.Client.Tests;

//тесты реестра протоколов, бейджей активного транспорта и сброса состояния при отключении
[Collection("GlobalStaticState")]
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

    [Theory]
    [InlineData(null, "AUTO", true)]
    [InlineData("", "AUTO", true)]
    [InlineData("auto", "AUTO", true)]
    [InlineData("stream", "OBXODKA-STREAM", true)]
    [InlineData("ws", "OBXODKA-WS", true)]
    [InlineData("websocket", "OBXODKA-WS", true)]
    [InlineData("xhttp", "OBXODKA-XHTTP", true)]
    [InlineData("reality", "OBXODKA-REALITY", true)]
    [InlineData("obx-v8", "OBXODKA-REALITY", true)]
    [InlineData("unknown-proto", "AUTO", false)]
    public void TryParsePreferredProtocol_NormalizesAliasesAndRejectsUnknown(string? raw, string expected, bool expectedOk)
    {
        var ok = ProtocolPipelineRegistry.TryParsePreferredProtocol(raw, out var normalized);
        Assert.Equal(expectedOk, ok);
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void PreferredProtocol_PersistsAndSyncsLegacyForceWebSocket()
    {
        var prevProto = PlatformServices.Preferences.Get(AppPreferenceKeys.PreferredProtocol, string.Empty);
        var prevWs = PlatformServices.Preferences.Get(AppPreferenceKeys.ForceWebSocket, false);
        try
        {
            PlatformServices.Preferences.Remove(AppPreferenceKeys.PreferredProtocol);
            PlatformServices.Preferences.Set(AppPreferenceKeys.ForceWebSocket, true);
            Assert.Equal("OBXODKA-WS", ProtocolPipelineRegistry.GetPreferredProtocol());

            _ = ProtocolPipelineRegistry.SetPreferredProtocol("reality");
            Assert.Equal("OBXODKA-REALITY", ProtocolPipelineRegistry.GetPreferredProtocol());
            Assert.False(PlatformServices.Preferences.Get(AppPreferenceKeys.ForceWebSocket, true));

            _ = ProtocolPipelineRegistry.SetPreferredProtocol("ws");
            Assert.Equal("OBXODKA-WS", ProtocolPipelineRegistry.GetPreferredProtocol());
            Assert.True(PlatformServices.Preferences.Get(AppPreferenceKeys.ForceWebSocket, false));

            _ = ProtocolPipelineRegistry.SetPreferredProtocol("auto");
            Assert.Equal("AUTO", ProtocolPipelineRegistry.GetPreferredProtocol());
            Assert.False(PlatformServices.Preferences.Get(AppPreferenceKeys.ForceWebSocket, true));
        }
        finally
        {
            PlatformServices.Preferences.Set(AppPreferenceKeys.PreferredProtocol, prevProto);
            PlatformServices.Preferences.Set(AppPreferenceKeys.ForceWebSocket, prevWs);
        }
    }

    [Theory]
    [InlineData("OBXODKA-STREAM", TransportProtocolKind.ObxodkaStream)]
    [InlineData("OBXODKA-WS", TransportProtocolKind.ObxodkaWebSocket)]
    [InlineData("OBXODKA-XHTTP", TransportProtocolKind.ObxodkaXhttp)]
    [InlineData("OBXODKA-REALITY", TransportProtocolKind.ObxodkaReality)]
    public void BuildProtocolRotationRing_FiltersStrictlyWhenManualProtocolSelected(string preferred, TransportProtocolKind expectedKind)
    {
        List<VpnServerDto> servers =
        [
            new(AppConfig.PrimaryDomain, 443, "Cloudflare Shield", true, 5, null, "Cloudflare", "EU", "shield"),
            new(AppConfig.DirectServerIp, 443, "Stockholm Main", true, 12, "TESTHASH==", "Vultr", "SE", "main")
        ];

        var ring = SniEngine.BuildProtocolRotationRing(servers, AppConfig.DirectServerIp, 443, null, false, preferred);

        Assert.NotEmpty(ring);
        Assert.All(ring, step => Assert.Equal(expectedKind, step.Kind));
    }

    [Fact]
    public void CliCommandParser_ParsesProtocolOptionAndAlias()
    {
        var parsed1 = obxodka.Cli.CliCommandParser.Parse(["start", "se-sto", "--protocol", "reality", "--json"]);
        Assert.Equal("connect", parsed1.Command);
        Assert.Equal("reality", parsed1.GetOption("protocol"));
        Assert.True(parsed1.IsJson);

        var parsed2 = obxodka.Cli.CliCommandParser.Parse(["connect", "--proto=xhttp"]);
        Assert.Equal("xhttp", parsed2.GetOption("proto"));
    }
}
