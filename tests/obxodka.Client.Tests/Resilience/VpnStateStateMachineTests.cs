namespace obxodka.Client.Tests;

[Trait("Category", "Unit")]
public class VpnStateStateMachineTests
{
    [Theory]
    [InlineData(AppVpnState.Disconnected, AppVpnState.Connecting, true)]
    [InlineData(AppVpnState.Connecting, AppVpnState.Connected, true)]
    [InlineData(AppVpnState.Connected, AppVpnState.Disconnecting, true)]
    [InlineData(AppVpnState.Disconnecting, AppVpnState.Disconnected, true)]
    [InlineData(AppVpnState.Connected, AppVpnState.Reconnecting, true)]
    [InlineData(AppVpnState.Reconnecting, AppVpnState.Connected, true)]
    [InlineData(AppVpnState.Connecting, AppVpnState.Error, true)]
    [InlineData(AppVpnState.Reconnecting, AppVpnState.Error, true)]
    [InlineData(AppVpnState.Error, AppVpnState.Disconnected, true)]
    public void ValidateStateTransitions(AppVpnState current, AppVpnState next, bool isValid)
    {
        var valid = (current, next) switch
        {
            (AppVpnState.Disconnected, AppVpnState.Connecting) => true,
            (AppVpnState.Connecting, AppVpnState.Connected) => true,
            (AppVpnState.Connecting, AppVpnState.Error) => true,
            (AppVpnState.Connected, AppVpnState.Disconnecting) => true,
            (AppVpnState.Connected, AppVpnState.Reconnecting) => true,
            (AppVpnState.Reconnecting, AppVpnState.Connected) => true,
            (AppVpnState.Reconnecting, AppVpnState.Error) => true,
            (AppVpnState.Disconnecting, AppVpnState.Disconnected) => true,
            (AppVpnState.Error, AppVpnState.Disconnected) => true,
            (AppVpnState.Error, AppVpnState.Connecting) => true,
            _ => false
        };

        Assert.Equal(isValid, valid);
    }

    [Theory]
    [InlineData(AppVpnState.Connected, true)]
    [InlineData(AppVpnState.Connecting, false)]
    [InlineData(AppVpnState.Disconnected, false)]
    [InlineData(AppVpnState.Error, false)]
    [InlineData(AppVpnState.Disconnecting, false)]
    public void IsActiveTrafficTunnelingState(AppVpnState state, bool expectedActive)
    {
        var isActive = state == AppVpnState.Connected;
        Assert.Equal(expectedActive, isActive);
    }
}
