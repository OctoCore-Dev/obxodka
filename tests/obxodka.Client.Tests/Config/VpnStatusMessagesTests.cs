namespace obxodka.Client.Tests.Config;

[Trait("Category", "Unit")]
public class VpnStatusMessagesTests
{
    [Fact]
    public void ConstantsAreNotEmptyAndValid()
    {
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.Disconnected));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.Connected));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.Protected));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.Connecting));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.Disconnecting));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.PleaseWait));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.Retry));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.StartAction));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.StopAction));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.ConnectAction));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.DisconnectAction));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.IpNotAssigned));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.IpAcquiring));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.DefaultConnectionError));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.CleaningOldSettings));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.ResolvingFastestNode));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.InitializingWintunAdapter));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.ApplyingNetworkSettings));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.CheckingTunnelReadiness));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.CheckingChannelDuplex));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.TxTransmissionFailed));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.RedirectingTraffic));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.EnablingDnsProtection));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.ProtectedConnectionActive));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.PacketLossDetected));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.PacketBlockDetected));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.ConnectionRestored));
        Assert.False(string.IsNullOrWhiteSpace(VpnStatusMessages.ConnectionRestoredTls));
    }

    [Fact]
    public void DynamicMtuFormattersIncludeRealNumbers()
    {
        var scanMsg = VpnStatusMessages.ScanningMtuHole(1420, 1280, 8);
        Assert.Contains("1420", scanMsg);
        Assert.Contains("1280", scanMsg);
        Assert.Contains("8", scanMsg);

        var probeMsg = VpnStatusMessages.ProbingMtuHole(1420, 1280, 2);
        Assert.Contains("1420", probeMsg);
        Assert.Contains("1280", probeMsg);
        Assert.Contains("#2", probeMsg);

        var duplexProbeMsg = VpnStatusMessages.ProbingDuplex(1420, 1280, 3, 500, 200);
        Assert.Contains("500", duplexProbeMsg);
        Assert.Contains("200", duplexProbeMsg);
        Assert.Contains("#3", duplexProbeMsg);

        var candidateMsg = VpnStatusMessages.TestingMtuCandidate(1360);
        Assert.Contains("1360", candidateMsg);

        var holeMsg = VpnStatusMessages.MtuHoleFound(1360);
        Assert.Contains("1360", holeMsg);

        var holePingMsg = VpnStatusMessages.MtuHoleFoundWithPing(1360, 42);
        Assert.Contains("1360", holePingMsg);
        Assert.Contains("42", holePingMsg);

        var syncMsg = VpnStatusMessages.MtuSyncingWithServer(1360);
        Assert.Contains("1360", syncMsg);

        var lockedMsg = VpnStatusMessages.MtuLocked(1360);
        Assert.Contains("1360", lockedMsg);

        var pingMsg = VpnStatusMessages.PingReceived(25);
        Assert.Contains("25", pingMsg);

        var rxMsg = VpnStatusMessages.DownlinkVerified(1024);
        Assert.Contains("1024", rxMsg);

        var duplexMsg = VpnStatusMessages.ChannelVerifiedDuplex(512, 1024);
        Assert.Contains("512", duplexMsg);
        Assert.Contains("1024", duplexMsg);

        var secureDuplexMsg = VpnStatusMessages.ChannelVerifiedSecureDuplex(512, 1024);
        Assert.Contains("512", secureDuplexMsg);
        Assert.Contains("1024", secureDuplexMsg);

        var secureMsg = VpnStatusMessages.DownlinkVerifiedSecure(2048);
        Assert.Contains("2048", secureMsg);

        var rxTimeoutMsg = VpnStatusMessages.RxResponseTimeout(768);
        Assert.Contains("768", rxTimeoutMsg);
    }

    [Fact]
    public void NetworkFormattersIncludeArguments()
    {
        var routeMsg = VpnStatusMessages.BuildingRoute("obxodka.one");
        Assert.Contains("obxodka.one", routeMsg);

        var domainMsg = VpnStatusMessages.DomainRoute("example.com");
        Assert.Contains("example.com", domainMsg);

        var nodeMsg = VpnStatusMessages.ConnectingToNode("1.2.3.4", 443);
        Assert.Contains("1.2.3.4:443", nodeMsg);

        var serverMsg = VpnStatusMessages.ConnectingToServer("5.6.7.8", 8443);
        Assert.Contains("5.6.7.8:8443", serverMsg);

        var ipMsg = VpnStatusMessages.IpAssigned("100.64.0.2");
        Assert.Contains("100.64.0.2", ipMsg);

        var initAdapterMsg = VpnStatusMessages.InitializingAdapter("Obxodka");
        Assert.Contains("Obxodka", initAdapterMsg);

        var startAdapterMsg = VpnStatusMessages.StartingAdapter("Obxodka");
        Assert.Contains("Obxodka", startAdapterMsg);

        var retryMsg = VpnStatusMessages.ReconnectingAttempt(2, 2);
        Assert.Contains("2/2", retryMsg);

        var backupNodeMsg = VpnStatusMessages.SwitchingToBackupNode("9.9.9.9");
        Assert.Contains("9.9.9.9", backupNodeMsg);

        var backupServerMsg = VpnStatusMessages.SwitchingToBackupServer("8.8.8.8");
        Assert.Contains("8.8.8.8", backupServerMsg);

        var nodeUnavailMsg = VpnStatusMessages.NodeUnavailableTryingBackup("1.1.1.1");
        Assert.Contains("1.1.1.1", nodeUnavailMsg);

        var serverUnavailMsg = VpnStatusMessages.ServerUnavailableTryingBackup("2.2.2.2");
        Assert.Contains("2.2.2.2", serverUnavailMsg);

        var noTrafficMsg = VpnStatusMessages.NoIncomingTraffic(1, 2);
        Assert.Contains("1/2", noTrafficMsg);

        var vpnWarnMsg = VpnStatusMessages.ThirdPartyVpnWarning("OtherVPN");
        Assert.Contains("OtherVPN", vpnWarnMsg);
    }
}
