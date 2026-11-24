namespace obxodka.Client.Tests.Protocols;

[Collection("GlobalStaticState")]
[Trait("Category", "Unit")]
[Trait("Category", "Protocols")]
public sealed class ProtocolRotationAndXhttpTests
{
    [Fact]
    public void BuildProtocolRotationRingStartsWithObxodkaStreamThenWsThenXhttp()
    {
        var prevCert = OctopusEngine.DirectOriginCertHash;
        OctopusEngine.ClearByteCappedNodes();
        try
        {
            List<VpnServerDto> servers =
            [
                new(AppConfig.PrimaryDomain, 443, "Cloudflare Shield", true, 5, null, "Cloudflare", "EU", "shield"),
                new(AppConfig.DirectServerIp, 443, "Amsterdam Main", true, 12, "TESTHASH_MAIN==", "Vultr", "NL", "main")
            ];

            var ring = SniEngine.BuildProtocolRotationRing(servers, AppConfig.PrimaryDomain, 443, null, false);

            Assert.True(ring.Count >= 3);
            Assert.Equal(TransportProtocolKind.ObxodkaStream, ring[0].Kind);
            Assert.Equal(AppConfig.DirectServerIp, ring[0].Host);
            Assert.False(ring[0].IsShield);
            Assert.Equal("TESTHASH_MAIN==", OctopusEngine.DirectOriginCertHash);

            var wsIndex = ring.FindIndex(s => s.Kind == TransportProtocolKind.ObxodkaWebSocket);
            var xhttpShieldIndex = ring.FindIndex(s => s.Kind == TransportProtocolKind.ObxodkaXhttp && s.IsShield);
            var xhttpDirectIndex = ring.FindIndex(s => s.Kind == TransportProtocolKind.ObxodkaXhttp && !s.IsShield);

            Assert.True(wsIndex > 0);
            Assert.True(xhttpShieldIndex > wsIndex);
            Assert.True(xhttpDirectIndex > xhttpShieldIndex);
        }
        finally
        {
            OctopusEngine.DirectOriginCertHash = prevCert;
            OctopusEngine.ClearByteCappedNodes();
        }
    }

    [Fact]
    public void BuildProtocolRotationRingMovesByteCappedDirectNodeAfterCloudflare()
    {
        OctopusEngine.ClearByteCappedNodes();
        try
        {
            OctopusEngine.MarkNodeByteCapped(AppConfig.DirectServerIp);
            List<VpnServerDto> servers =
            [
                new(AppConfig.DirectServerIp, 443, "Amsterdam Main", true, 12, "HASH==", "Vultr", "NL", "main"),
                new(AppConfig.PrimaryDomain, 443, "Shield", true, 5, null, "Cloudflare", "EU", "shield")
            ];

            var ring = SniEngine.BuildProtocolRotationRing(servers, AppConfig.DirectServerIp, 443, null, false);

            Assert.Equal(TransportProtocolKind.ObxodkaWebSocket, ring[0].Kind);
            Assert.Equal(TransportProtocolKind.ObxodkaXhttp, ring[1].Kind);
            Assert.True(ring[1].IsShield);
            Assert.Equal(TransportProtocolKind.ObxodkaStream, ring[^1].Kind);
            var directXhttpIndex = ring.FindIndex(s => s.Kind == TransportProtocolKind.ObxodkaXhttp && !s.IsShield);
            Assert.True(directXhttpIndex > 1);
        }
        finally
        {
            OctopusEngine.ClearByteCappedNodes();
        }
    }

    [Fact]
    public void EdgeEndpointHealthFilterAndRotateMovesUnhealthyIpToEnd()
    {
        EdgeEndpointHealth.ClearAll();
        try
        {
            var badIp = IPAddress.Parse("188.114.96.1");
            var goodIp1 = IPAddress.Parse("104.21.48.10");
            var goodIp2 = IPAddress.Parse("172.67.150.20");
            EdgeEndpointHealth.MarkUnhealthy(badIp.ToString(), 60);

            var rotated = EdgeEndpointHealth.FilterAndRotate([badIp, goodIp1, goodIp2]);

            Assert.Equal(3, rotated.Length);
            Assert.Equal(badIp, rotated[^1]);
            Assert.NotEqual(badIp, rotated[0]);
            Assert.True(EdgeEndpointHealth.IsUnhealthy(badIp.ToString()));
        }
        finally
        {
            EdgeEndpointHealth.ClearAll();
        }
    }

    [Fact]
    public void ProtocolPipelineRegistryResolvesBuiltInsAndCustomFactories()
    {
        Assert.True(ProtocolPipelineRegistry.TryGetFactory(TransportProtocolKind.ObxodkaStream, out var streamFactory));
        Assert.Equal("OBXODKA-STREAM", streamFactory.DisplayName);

        Assert.True(ProtocolPipelineRegistry.TryGetFactory(TransportProtocolKind.ObxodkaWebSocket, out var wsFactory));
        Assert.Equal("OBXODKA-WS", wsFactory.DisplayName);

        Assert.True(ProtocolPipelineRegistry.TryGetFactory(TransportProtocolKind.ObxodkaXhttp, out var xhttpFactory));
        Assert.Equal("OBXODKA-XHTTP", xhttpFactory.DisplayName);

        using var xhttp = Assert.IsType<ObxodkaXhttpTransport>(
            ProtocolPipelineRegistry.CreateTransport(new ProtocolRouteStep(
                TransportProtocolKind.ObxodkaXhttp,
                AppConfig.PrimaryDomain,
                443,
                AppConfig.PrimaryDomain,
                true,
                true)));
        Assert.Equal("OBXODKA-XHTTP", xhttp.ProtocolName);
        Assert.False(xhttp.IsConnected);
        Assert.Null(xhttp.RemoteConnectedEndpointIp);

        var customKind = (TransportProtocolKind)99;
        var customFactory = new StubProtocolFactory(customKind, "OBXODKA-FUTURE");
        ProtocolPipelineRegistry.RegisterFactory(customFactory);
        try
        {
            Assert.True(ProtocolPipelineRegistry.TryGetFactory(customKind, out var resolved));
            Assert.Equal("OBXODKA-FUTURE", resolved.DisplayName);
        }
        finally
        {
            _ = ProtocolPipelineRegistry.UnregisterFactory(customKind);
        }
    }

    [Fact]
    public void TransportFrameControlMapperClassifiesAllControlAndDataFrames()
    {
        byte[] mtuPkt = [0xCC, 0xDD, 0x05, 0x00];
        Assert.Equal(ObxodkaFraming.CmdMtuSync,
            TransportFrameControlMapper.ClassifyOutgoingPacket(mtuPkt, mtuPkt.Length, out var off1, out var len1));
        Assert.Equal(2, off1);
        Assert.Equal(2, len1);

        byte[] burstPkt = [0xEE, 0x77];
        Assert.Equal(ObxodkaFraming.CmdMtuBurstRequest,
            TransportFrameControlMapper.ClassifyOutgoingPacket(burstPkt, burstPkt.Length, out _, out var len2));
        Assert.Equal(0, len2);

        byte[] pongPkt = [0xEE, 0x88, 1, 2, 3, 4];
        Assert.Equal(ObxodkaFraming.CmdPong,
            TransportFrameControlMapper.ClassifyOutgoingPacket(pongPkt, pongPkt.Length, out var off3, out var len3));
        Assert.Equal(2, off3);
        Assert.Equal(4, len3);

        byte[] disconnectPkt = [0xEE, 0x99];
        Assert.Equal(ObxodkaFraming.CmdDisconnect,
            TransportFrameControlMapper.ClassifyOutgoingPacket(disconnectPkt, disconnectPkt.Length, out _, out _));

        byte[] pingMarkerPkt = [0xAA, 0xBB, 1, 2, 3, 4, 5, 6, 7, 8];
        Assert.Equal(ObxodkaFraming.CmdPing,
            TransportFrameControlMapper.ClassifyOutgoingPacket(pingMarkerPkt, pingMarkerPkt.Length, out var off5, out var len5));
        Assert.Equal(2, off5);
        Assert.Equal(8, len5);

        var rawData = new byte[64];
        rawData[0] = 0x45;
        Assert.Equal(ObxodkaFraming.CmdData,
            TransportFrameControlMapper.ClassifyOutgoingPacket(rawData, rawData.Length, out var off6, out var len6));
        Assert.Equal(0, off6);
        Assert.Equal(64, len6);
    }

    [Fact]
    public void SniEngineBuildXhttpAndStreamCandidatesFollowPriorityRules()
    {
        var xhttpCandidates = SniEngine.BuildXhttpCandidates(AppConfig.PrimaryDomain, 443, null);
        Assert.NotEmpty(xhttpCandidates);
        Assert.Contains("octocore.dev", xhttpCandidates[0].ServerHost, StringComparison.OrdinalIgnoreCase);

        var streamCandidates = SniEngine.BuildStreamCandidates(AppConfig.DirectServerIp, 443, null);
        Assert.Equal(4, streamCandidates.Count);
        Assert.True(streamCandidates[0].EnableDpiBypass);
    }

    [Fact]
    public void VpnStatusMessagesFormatsProtocolRotationStatuses()
    {
        Assert.Equal("Obxodka Stream", VpnStatusMessages.FormatProtocolName("OBXODKA-STREAM"));
        Assert.Equal("Cloudflare WS", VpnStatusMessages.FormatProtocolName("OBXODKA-WS", true));
        Assert.Equal("Cloudflare XHTTP", VpnStatusMessages.FormatProtocolName("OBXODKA-XHTTP", true));
        Assert.Equal("Direct XHTTP", VpnStatusMessages.FormatProtocolName("OBXODKA-XHTTP"));
        Assert.Contains("Cloudflare WS", VpnStatusMessages.TryingProtocol("Cloudflare WS", 2, 4), StringComparison.Ordinal);
        Assert.Contains("Cloudflare XHTTP", VpnStatusMessages.ProtocolUnavailableSwitching("Obxodka Stream", "Cloudflare XHTTP"),
            StringComparison.Ordinal);
        Assert.Contains("Direct XHTTP", VpnStatusMessages.RotatingProtocol("Direct XHTTP"), StringComparison.Ordinal);
        Assert.Contains("Cloudflare WS", VpnStatusMessages.ProtocolSwitched("Cloudflare WS"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("OBXODKA-STREAM", 4, "4L")]
    [InlineData("OBXODKA-WS", 1, "WS")]
    [InlineData("OBXODKA-XHTTP", 1, "XHTTP")]
    [InlineData("AUTO", 2, "2L")]
    public void ProtocolPipelineRegistryResolveActiveTagExhaustivelyFormatsProtocols(string protocolName, int activeRays, string expected)
    {
        var formatted = ProtocolPipelineRegistry.ResolveActiveTag(protocolName, activeRays) switch
        {
            StreamTransportTag stream => $"{stream.ActiveRays}L",
            WebSocketTransportTag => "WS",
            XhttpTransportTag => "XHTTP"
        };

        Assert.Equal(expected, formatted);
    }

    private sealed class StubProtocolFactory(TransportProtocolKind kind, string displayName) : ITransportProtocolFactory
    {
        public TransportProtocolKind Kind { get; } = kind;
        public string DisplayName { get; } = displayName;

        public IVpnTransport CreateTransport(ProtocolRouteStep step, string? preferredAssignedIp) =>
            new ObxodkaXhttpTransport(step.Port, step.TargetSni, preferredAssignedIp);
    }
}
