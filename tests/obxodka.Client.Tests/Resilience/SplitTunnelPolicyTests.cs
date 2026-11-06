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

    [Fact]
    public void NormalizeRuleEntry_PreservesIpv6AndBracketedPorts()
    {
        Assert.Equal("2a02:6b8::1", SplitTunnelPolicy.NormalizeRuleEntry("2a02:6b8::1"));
        Assert.Equal("2a02:6b8::1", SplitTunnelPolicy.NormalizeRuleEntry("[2a02:6b8::1]:8080"));
        Assert.Equal("2001:db8::ff00:42:8329", SplitTunnelPolicy.NormalizeRuleEntry("2001:db8::ff00:42:8329"));
        Assert.Equal("fe80::1", SplitTunnelPolicy.NormalizeRuleEntry("[fe80::1]:443"));
    }

    [Fact]
    public void NormalizeRuleEntry_PreservesCidrSubnetsAndStripsUrls()
    {
        Assert.Equal("192.168.1.0/24", SplitTunnelPolicy.NormalizeRuleEntry("192.168.1.0/24"));
        Assert.Equal("10.0.0.0/8", SplitTunnelPolicy.NormalizeRuleEntry("10.0.0.0/8"));
        Assert.Equal("2a02:6b8::/32", SplitTunnelPolicy.NormalizeRuleEntry("2a02:6b8::/32"));
        Assert.Equal("example.com", SplitTunnelPolicy.NormalizeRuleEntry("https://example.com/api/v1/user?id=123"));
        Assert.Equal("api.service.ru", SplitTunnelPolicy.NormalizeRuleEntry("http://api.service.ru:8443/status"));
    }

    [Fact]
    public void SplitTunnelPolicy_MatchesExpandedRussianPresets()
    {
        var policy = new SplitTunnelPolicy
        {
            BypassRuGov = true,
            BypassRuBanks = true,
            BypassRuMedia = true,
            BypassRuMarketplaces = true,
            BypassAllRuTlds = false
        };

        // Гос-сервисы
        Assert.True(policy.IsDomainBypassed("emias.info"));
        Assert.True(policy.IsDomainBypassed("nalog.gov.ru"));
        Assert.True(policy.IsDomainBypassed("crpt.ru"));
        Assert.True(policy.IsDomainBypassed("cbr.ru"));

        // Банки
        Assert.True(policy.IsDomainBypassed("pochtabank.ru"));
        Assert.True(policy.IsDomainBypassed("mkb.ru"));
        Assert.True(policy.IsDomainBypassed("uralsib.ru"));
        Assert.True(policy.IsDomainBypassed("sbp.nspk.ru"));

        // Медиа
        Assert.True(policy.IsDomainBypassed("habr.com"));
        Assert.True(policy.IsDomainBypassed("2gis.ru"));
        Assert.True(policy.IsDomainBypassed("vkvideo.ru"));
        Assert.True(policy.IsDomainBypassed("rutube.ru"));

        // Маркетплейсы
        Assert.True(policy.IsDomainBypassed("market.yandex.ru"));
        Assert.True(policy.IsDomainBypassed("megamarket.ru"));
        Assert.True(policy.IsDomainBypassed("dns-shop.ru"));
    }

    [Fact]
    public void GetActiveBypassRoutes_ReturnsIsolatedSnapshot()
    {
        var policy = new SplitTunnelPolicy();
        policy.RecordBypassRoute("198.51.100.1", "Initial");

        var snapshot = policy.GetActiveBypassRoutes();
        Assert.Single(snapshot);
        Assert.Equal("198.51.100.1", snapshot[0].DestinationIp);

        // Мутация возвращенного списка не должна влиять на внутреннее состояние полиси
        snapshot.Clear();
        Assert.Empty(snapshot);
        Assert.Single(policy.ActiveBypassRoutes);

        // Добавление нового маршрута корректно отражается в новом снимке
        policy.RecordBypassRoute("198.51.100.2", "Second");
        var updatedSnapshot = policy.GetActiveBypassRoutes();
        Assert.Equal(2, updatedSnapshot.Count);
    }
}
