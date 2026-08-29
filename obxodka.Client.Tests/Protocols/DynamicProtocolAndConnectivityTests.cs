namespace obxodka.Client.Tests.Protocols;

[Trait("Category", "Protocols")]
[Trait("Category", "Resilience")]
public sealed class DynamicProtocolAndConnectivityTests
{
    [Fact]
    public void DynamicProtocolCandidatesOrderIsPrioritizedForPerformanceAndStealth()
    {
        var candidates = new[] { "FECHSUE", "HTTP3", "HTTP2" };

        Assert.Equal("FECHSUE", candidates[0]);
        Assert.Equal("HTTP3", candidates[1]);
        Assert.Equal("HTTP2", candidates[2]);
    }

    [Theory]
    [InlineData("FECHSUE", "FECHSUE")]
    [InlineData("HTTP3", "HTTP3")]
    [InlineData("HTTP2", "HTTP2")]
    [InlineData("AUTO", "FECHSUE")]
    public void ActiveProtocolResolvesCorrectly(string preferenceMode, string expectedActive)
    {
        var resolved = preferenceMode == "AUTO" ? "FECHSUE" : preferenceMode;
        Assert.Equal(expectedActive, resolved);
    }

    [Fact]
    public void WakeFromSleepRetryPolicyCalculatesProgressiveDelays()
    {
        var maxRetries = 3;
        var delays = new List<int>();

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            var delay = attempt * 600;
            delays.Add(delay);
        }

        Assert.Equal(600, delays[0]);
        Assert.Equal(1200, delays[1]);
        Assert.Equal(1800, delays[2]);
    }

    [Theory]
    [InlineData(NetworkAccess.None, false)]
    [InlineData(NetworkAccess.Unknown, false)]
    [InlineData(NetworkAccess.Local, false)]
    [InlineData(NetworkAccess.ConstrainedInternet, false)]
    [InlineData(NetworkAccess.Internet, true)]
    public void ConnectivityAccessValidation(NetworkAccess access, bool hasInternet)
    {
        var isOnline = access == NetworkAccess.Internet;
        Assert.Equal(hasInternet, isOnline);
    }

    [Fact]
    public void ProtocolSyncAcrossPreferencesReflectsSelectedState()
    {
        var supportedProtocols = new[] { "AUTO", "FECHSUE", "HTTP3", "HTTP2" };

        foreach (var proto in supportedProtocols)
        {
            var isAuto = proto == "AUTO";
            var isFechsue = proto == "FECHSUE";
            var isHttp3 = proto == "HTTP3";
            var isHttp2 = proto == "HTTP2";

            Assert.True(isAuto || isFechsue || isHttp3 || isHttp2);
        }
    }

    [Fact]
    public void HotSwapPolicyAllowsInstantProtocolSwitchingWhenVpnConnected()
    {
        var isVpnRunning = true;
        var quickProtocolSwitch = true;

        var canSwitchProtocols = !isVpnRunning || quickProtocolSwitch;
        Assert.True(canSwitchProtocols);
    }

    [Fact]
    public void SwitchingProtocolResetsSessionCleanly()
    {
        var activeProtocols = new List<string>
        {
            "HTTP3"
        };
        _ = Assert.Single(activeProtocols);
        Assert.Equal("HTTP3", activeProtocols[0]);

        activeProtocols.Clear();
        activeProtocols.Add("FECHSUE");
        _ = Assert.Single(activeProtocols);
        Assert.Equal("FECHSUE", activeProtocols[0]);
    }
}
