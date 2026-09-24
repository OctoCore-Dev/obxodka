using obxodka.Core.Models;

namespace obxodka.Client.Tests.Resilience;

[Trait("Category", "Unit")]
public class SplitTunnelPolicyTests
{
    [Fact]
    public void SplitTunnelPolicyDefaultStateIsEnabledWithRemoteManagement()
    {
        var policy = new SplitTunnelPolicy();

        Assert.True(policy.Enabled);
        Assert.True(policy.BypassRemoteManagement);
        Assert.Empty(policy.CustomBypassIps);
        Assert.Empty(policy.ActiveBypassRoutes);
    }

    [Fact]
    public void SplitTunnelPolicyRecordsAndClearsActiveRoutes()
    {
        var policy = new SplitTunnelPolicy();

        policy.RecordBypassRoute("198.51.100.25", "AnyDesk Session");
        policy.RecordBypassRoute("203.0.113.10", "Custom Rule");

        Assert.Equal(2, policy.ActiveBypassRoutes.Count);
        Assert.Equal("198.51.100.25", policy.ActiveBypassRoutes[0].DestinationIp);
        Assert.Equal("AnyDesk Session", policy.ActiveBypassRoutes[0].Reason);
        Assert.Equal("203.0.113.10", policy.ActiveBypassRoutes[1].DestinationIp);

        policy.ClearActiveRoutes();
        Assert.Empty(policy.ActiveBypassRoutes);
    }

    [Fact]
    public void SplitTunnelPolicyIgnoresNullOrEmptyIps()
    {
        var policy = new SplitTunnelPolicy();

        policy.RecordBypassRoute("", "Invalid");
        policy.RecordBypassRoute("   ", "Invalid");

        Assert.Empty(policy.ActiveBypassRoutes);
    }
}
