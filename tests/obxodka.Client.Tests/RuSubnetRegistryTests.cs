using System.Net;
using obxodka.Shared.Geo;
using Xunit;

namespace obxodka.Client.Tests;

//тесты высокопроизводительного реестра подсетей рф (dir-24 flat bitmap)
public sealed class RuSubnetRegistryTests
{
    [Fact]
    public void RuSubnetRegistry_Loads_And_Detects_Russian_Ips()
    {
        //яндекс dns (77.88.8.8) - гарантированно рф
        var yandexIp = IPAddress.Parse("77.88.8.8");
        Assert.True(RuSubnetRegistry.IsRuIpv4(yandexIp));

        //сбербанк (194.54.14.1) - гарантированно рф
        var sberIp = IPAddress.Parse("194.54.14.1");
        Assert.True(RuSubnetRegistry.IsRuIpv4(sberIp));

        //ростелеком спб (178.64.1.1) - гарантированно рф
        var rostelecomIp = IPAddress.Parse("178.64.1.1");
        Assert.True(RuSubnetRegistry.IsRuIpv4(rostelecomIp));

        //селектел спб (95.213.128.1) - гарантированно рф
        var selectelIp = IPAddress.Parse("95.213.128.1");
        Assert.True(RuSubnetRegistry.IsRuIpv4(selectelIp));
    }

    [Fact]
    public void RuSubnetRegistry_Rejects_Foreign_Ips()
    {
        //google dns (8.8.8.8) - сша
        var googleIp = IPAddress.Parse("8.8.8.8");
        Assert.False(RuSubnetRegistry.IsRuIpv4(googleIp));

        //cloudflare dns (1.1.1.1) - австралия/anycast сша
        var cloudflareIp = IPAddress.Parse("1.1.1.1");
        Assert.False(RuSubnetRegistry.IsRuIpv4(cloudflareIp));

        //vultr швеция стокгольм (70.34.201.253) - швеция/сша
        var vultrIp = IPAddress.Parse("70.34.201.253");
        Assert.False(RuSubnetRegistry.IsRuIpv4(vultrIp));
    }

    [Fact]
    public void RuSubnetRegistry_GetTier1Supernets_Returns_Valid_Routes()
    {
        var supernets = RuSubnetRegistry.GetTier1Supernets(18);
        Assert.NotEmpty(supernets);

        foreach (var (net, mask) in supernets)
        {
            Assert.True(System.Numerics.BitOperations.PopCount(mask) <= 18); //префикс /18 или шире
            Assert.Equal(net, net & mask);
        }
    }
}
