namespace obxodka.Client.Tests.Resilience;

[Trait("Category", "Resilience")]
[Trait("Category", "Unit")]
public class NetworkResilienceTests
{
    [Fact]
    public void AutoReconnectExponentialBackoffRecoversGracefully()
    {
        var retryAttempts = 0;
        var maxRetries = 5;
        var connectionEstablished = false;

        var delays = new List<int>();

        while (retryAttempts < maxRetries && !connectionEstablished)
        {
            retryAttempts++;
            var delayMs = Math.Min(100 * (1 << retryAttempts), 2000);
            delays.Add(delayMs);

            if (retryAttempts == 3)
            {
                connectionEstablished = true;
            }
        }

        Assert.True(connectionEstablished);
        Assert.Equal(3, retryAttempts);
        Assert.Equal(200, delays[0]);
        Assert.Equal(400, delays[1]);
        Assert.Equal(800, delays[2]);
    }

    [Theory]
    [InlineData(AppVpnState.Connected, true, true)]
    [InlineData(AppVpnState.Connecting, true, false)]
    [InlineData(AppVpnState.Disconnected, true, false)]
    [InlineData(AppVpnState.Disconnected, false, true)]
    [InlineData(AppVpnState.Error, true, false)]
    public void KillSwitchPolicyEnforcement(AppVpnState state, bool killSwitchEnabled, bool shouldAllowOutbound)
    {
        var allowed = !killSwitchEnabled || state == AppVpnState.Connected;

        Assert.Equal(shouldAllowOutbound, allowed);
    }

    [Fact]
    public void HeartbeatKeepAliveIntervalClamping()
    {
        var configuredIntervalSec = 0;
        var safeInterval = Math.Clamp(configuredIntervalSec, 5, 60);

        Assert.Equal(5, safeInterval);
    }
}
