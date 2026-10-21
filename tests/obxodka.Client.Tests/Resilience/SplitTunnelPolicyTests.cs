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
        policy.RecordBypassRoute("198.51.100.25", "Duplicate Entry");
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

    [Fact]
    public void SplitTunnelPolicyMatchesRuPresetsAndSubdomains()
    {
        var policy = new SplitTunnelPolicy
        {
            BypassRuGov = true,
            BypassRuBanks = true,
            BypassRuMedia = false,
            BypassRuMarketplaces = true,
            BypassAllRuTlds = false
        };

        Assert.True(policy.IsDomainBypassed("gosuslugi.ru"));
        Assert.True(policy.IsDomainBypassed("esia.gosuslugi.ru"));
        Assert.True(policy.IsDomainBypassed("online.sberbank.ru"));
        Assert.True(policy.IsDomainBypassed("api.tbank.ru"));
        Assert.True(policy.IsDomainBypassed("www.ozon.ru"));
        Assert.False(policy.IsDomainBypassed("kinopoisk.ru"));
        Assert.False(policy.IsDomainBypassed("youtube.com"));

        policy.BypassAllRuTlds = true;
        Assert.True(policy.IsDomainBypassed("any-random-site.ru"));
        Assert.True(policy.IsDomainBypassed("portal.xn--p1ai"));
        Assert.False(policy.IsDomainBypassed("discord.com"));
    }

    [Fact]
    public void SplitTunnelPolicyMatchesCustomDomainsAndIps()
    {
        var policy = new SplitTunnelPolicy();
        policy.SetCustomRules([
            "https://Sub.MyCustomBank.org/auth?id=1",
            "*.corp.example.net",
            "185.71.76.10"
        ]);

        _ = Assert.Single(policy.CustomBypassIps);
        Assert.Equal("185.71.76.10", policy.CustomBypassIps[0]);
        Assert.True(policy.IsDomainBypassed("sub.mycustombank.org"));
        Assert.True(policy.IsDomainBypassed("api.sub.mycustombank.org"));
        Assert.True(policy.IsDomainBypassed("portal.corp.example.net"));
        Assert.False(policy.IsDomainBypassed("other.example.net"));
    }

    [Fact]
    public void SplitTunnelPolicyMatchesBypassedExecutables()
    {
        var policy = new SplitTunnelPolicy();
        policy.SetBypassedExecutables([
            "Telegram.exe",
            @"C:\Games\Counter-Strike\cs2.exe"
        ]);

        Assert.True(policy.HasBypassedExecutables);
        Assert.True(policy.IsProcessBypassed("telegram.exe", @"C:\Users\User\AppData\Roaming\Telegram Desktop\Telegram.exe"));
        Assert.True(policy.IsProcessBypassed("cs2.exe", @"D:\OtherPath\cs2.exe"));
        Assert.True(policy.IsProcessBypassed(null, @"C:\Games\Counter-Strike\cs2.exe"));
        Assert.False(policy.IsProcessBypassed("chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe"));
    }
}
