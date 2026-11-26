using obxodka.Core.Pipeline;

namespace obxodka.Client.Tests.Resilience;

[Collection("GlobalStaticState")]
[Trait("Category", "Unit")]
public sealed class VpnSessionOrchestratorTests : IDisposable
{
    private readonly Mock<ITunnelPlatformAdapter> _adapterMock = new(MockBehavior.Loose);

    public VpnSessionOrchestratorTests()
    {
        string? noConflict = null;
        _adapterMock.Setup(a => a.DetectConflictingVpn()).Returns(noConflict);
        _adapterMock.Setup(a => a.RunNetworkPreflightAsync()).ReturnsAsync(noConflict);
        _adapterMock.Setup(a => a.PreparePermissionsAndStartServiceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _adapterMock.Setup(a => a.CleanupBeforeConnectAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _adapterMock.Setup(a => a.PrepareBypassHostsAsync(It.IsAny<string[]>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _adapterMock.Setup(a => a.ShutdownTunnelAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        OctopusEngine.Current.ProtectTransportSockets(null);
        OctopusEngine.Current.EnsureExternalHostRoutesAsync = null;
        OctopusEngine.Current.Pipeline.BlockIpv6Traffic = false;
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task DetectConflictingVpnAndPreflightDelegateToPlatformAdapterAsync()
    {
        _adapterMock.Setup(a => a.DetectConflictingVpn()).Returns("WireGuard");
        _adapterMock.Setup(a => a.RunNetworkPreflightAsync()).ReturnsAsync("No uplink");

        using var orchestrator = new VpnSessionOrchestrator(_adapterMock.Object, OctopusEngine.Current, false);

        Assert.Equal("WireGuard", orchestrator.DetectConflictingVpn());
        Assert.Equal("No uplink", await orchestrator.RunNetworkPreflightAsync());
    }

    [Fact]
    public async Task StartVpnAsync_WhenPermissionDenied_ReportsErrorAndStopsCleanly()
    {
        _adapterMock.Setup(a => a.PreparePermissionsAndStartServiceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        using var orchestrator = new VpnSessionOrchestrator(_adapterMock.Object, OctopusEngine.Current, false);
        string? capturedError = null;
        orchestrator.OnErrorOccurred += msg => capturedError = msg;

        await orchestrator.StartVpnAsync("1.2.3.4", 443);

        Assert.Equal(AppVpnState.Error, orchestrator.CurrentState);
        Assert.Equal("VPN разрешение не выдано", capturedError);
    }

    [Fact]
    public async Task StartVpnAsync_WhenStoppedConcurrently_ResolvesToDisconnectedWithoutError()
    {
        var enteredCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _adapterMock
            .Setup(a => a.CleanupBeforeConnectAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                _ = enteredCleanup.TrySetResult();
                await releaseCleanup.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            });

        using var orchestrator = new VpnSessionOrchestrator(_adapterMock.Object, OctopusEngine.Current, false);
        var errors = new List<string>();
        orchestrator.OnErrorOccurred += msg => errors.Add(msg);

        var startTask = orchestrator.StartVpnAsync("1.2.3.4", 443);
        await enteredCleanup.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stopTask = orchestrator.StopVpnAsync();
        _ = releaseCleanup.TrySetResult();

        await Task.WhenAll(startTask, stopTask);

        Assert.Equal(AppVpnState.Disconnected, orchestrator.CurrentState);
        Assert.Empty(errors);
        _adapterMock.Verify(
            a => a.ShutdownTunnelAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), false),
            Times.AtLeastOnce);
    }

    [Fact]
    public void ProtocolPipelineRegistry_CreateTransport_InjectsEngineSocketProtector()
    {
        var protectedSockets = new List<Socket>();
        OctopusEngine.Current.ProtectTransportSockets(s => protectedSockets.Add(s));

        var mockTransport = new Mock<IVpnTransport>(MockBehavior.Loose);
        Action<Socket>? injectedProtector = null;
        mockTransport
            .Setup(t => t.ProtectSockets(It.IsAny<Action<Socket>>()))
            .Callback<Action<Socket>>(cb => injectedProtector = cb);

        var customFactory = new Mock<ITransportProtocolFactory>(MockBehavior.Strict);
        customFactory.SetupGet(f => f.Kind).Returns((TransportProtocolKind)999);
        customFactory.SetupGet(f => f.Order).Returns(999);
        customFactory.SetupGet(f => f.DisplayName).Returns("CUSTOM-TEST");
        customFactory
            .Setup(f => f.CreateTransport(It.IsAny<ProtocolRouteStep>(), It.IsAny<string?>()))
            .Returns(mockTransport.Object);

        ProtocolPipelineRegistry.RegisterFactory(customFactory.Object);
        try
        {
            var created = ProtocolPipelineRegistry.CreateTransport(
                new ProtocolRouteStep((TransportProtocolKind)999, "127.0.0.1", 443, null, false, false));

            Assert.Same(mockTransport.Object, created);
            Assert.NotNull(injectedProtector);

            using var testSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            injectedProtector!(testSocket);
            Assert.Single(protectedSockets, testSocket);
        }
        finally
        {
            _ = ProtocolPipelineRegistry.UnregisterFactory((TransportProtocolKind)999);
        }
    }

    [Theory]
    [InlineData(TransportProtocolKind.ObxodkaReality)]
    [InlineData(TransportProtocolKind.ObxodkaWebSocket)]
    [InlineData(TransportProtocolKind.ObxodkaXhttp)]
    public void ProtocolPipelineRegistry_BuiltInFactories_CreateDistinctTransports(TransportProtocolKind kind)
    {
        using var transport = ProtocolPipelineRegistry.CreateTransport(
            new ProtocolRouteStep(kind, "127.0.0.1", 443, "example.com", false, false));

        Assert.NotNull(transport);
    }

    [Theory]
    [InlineData(true, 6, true)]
    [InlineData(true, 4, false)]
    [InlineData(false, 6, false)]
    [InlineData(false, 4, false)]
    public void PacketPipeline_ShouldDropIpv6Packet_RespectsBlockIpv6TrafficSetting(
        bool blockIpv6,
        int ipVersion,
        bool expectedDrop)
    {
        var pipeline = new PacketPipeline(OctopusEngine.Current)
        {
            BlockIpv6Traffic = blockIpv6
        };

        var packet = new byte[40];
        packet[0] = (byte)(ipVersion << 4);

        Assert.Equal(expectedDrop, pipeline.ShouldDropIpv6Packet(packet));
    }

    [Theory]
    [InlineData(new byte[] { 192, 168, 0, 52 }, new byte[] { 1, 1, 1, 1 }, true)]
    [InlineData(new byte[] { 10, 8, 0, 2 }, new byte[] { 224, 0, 0, 251 }, true)]
    [InlineData(new byte[] { 10, 8, 0, 2 }, new byte[] { 1, 1, 1, 1 }, false)]
    public void PacketPipeline_ShouldDropOutgoingIpv4Noise_RejectsForeignSrcAndMulticast(
        byte[] srcIp,
        byte[] dstIp,
        bool expectedDrop)
    {
        var pipeline = new PacketPipeline(OctopusEngine.Current);
        var packet = new byte[28];
        packet[0] = 0x45;
        packet[9] = 17;
        srcIp.CopyTo(packet.AsSpan(12, 4));
        dstIp.CopyTo(packet.AsSpan(16, 4));

        Assert.Equal(expectedDrop, pipeline.ShouldDropOutgoingIpv4Noise(packet));
    }

    [Fact]
    public void PreferenceKeys_SsotConstantsMatchExpectedDefaults()
    {
        Assert.Equal("obxodka_win_preset_rutlds", AppPreferenceKeys.BypassRuSites);
        Assert.Equal("BlockIpv6", AppPreferenceKeys.BlockIpv6);
        Assert.True(PlatformServices.Preferences.Get(AppPreferenceKeys.BypassRuSites, true));
        Assert.False(PlatformServices.Preferences.Get(AppPreferenceKeys.BlockIpv6, false));
    }

    [Fact]
    public async Task StopVpnAsync_IgnoresSubsequentPlatformNetworkChangedAndLateLogsAsync()
    {
        using var orchestrator = new VpnSessionOrchestrator(_adapterMock.Object, OctopusEngine.Current, false);
        var logsAfterStop = new List<string>();

        await orchestrator.StopVpnAsync();
        orchestrator.OnLogUpdated += logsAfterStop.Add;

        _adapterMock.Raise(a => a.OnPlatformNetworkChanged += null);
        orchestrator.TriggerImmediateReconnect();
        await Task.Delay(350);

        Assert.Equal(AppVpnState.Disconnected, orchestrator.CurrentState);
        Assert.Empty(logsAfterStop);
    }
}
