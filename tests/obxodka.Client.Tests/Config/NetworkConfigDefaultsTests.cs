namespace obxodka.Client.Tests.Config;

[Trait("Category", "Unit")]
public class NetworkConfigDefaultsTests
{
    [Fact]
    public void SmartMtuEngineConstantsAreValid()
    {
        Assert.Equal(1280, SmartMtuEngine.DefaultMtu);
        Assert.Equal(1200, SmartMtuEngine.MinMtu);
        Assert.Equal(1500, SmartMtuEngine.MaxMtu);
    }

    [Fact]
    public void DnsDefaultsConstantsAreValid()
    {
        Assert.Equal("1.1.1.1", DnsDefaults.PrimaryDns);
        Assert.Equal("1.0.0.1", DnsDefaults.SecondaryDns);
        Assert.NotEmpty(DnsDefaults.TrustedDnsServers);
        Assert.Contains("1.1.1.1", DnsDefaults.TrustedDnsServers);
        Assert.Contains("8.8.8.8", DnsDefaults.TrustedDnsServers);
    }

    [Theory]
    [InlineData(0, 1200)]
    [InlineData(-100, 1200)]
    [InlineData(576, 1200)]
    [InlineData(1000, 1200)]
    [InlineData(1199, 1200)]
    [InlineData(1200, 1200)]
    [InlineData(1280, 1280)]
    [InlineData(1350, 1350)]
    [InlineData(1360, 1360)]
    [InlineData(1400, 1400)]
    [InlineData(1440, 1440)]
    [InlineData(1441, 1441)]
    [InlineData(1500, 1500)]
    [InlineData(9000, 1500)]
    public void ClampMtuClampsCorrectly(int input, int expected)
    {
        var result = SmartMtuEngine.ClampMtu(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void TrustedDnsServersAllParseAsValidIpAddresses()
    {
        foreach (var ip in DnsDefaults.TrustedDnsServers)
        {
            var parsed = IPAddress.TryParse(ip, out var addr);
            Assert.True(parsed);
            Assert.NotNull(addr);
        }
    }

    [Fact]
    public async Task GetHealthyDnsServersReturnsValidServersAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var servers = await DnsProbeService.GetHealthyDnsServersAsync(timeoutMs: 300, ct: cts.Token);
        Assert.NotNull(servers);
        Assert.NotEmpty(servers);
        foreach (var server in servers)
        {
            Assert.True(IPAddress.TryParse(server, out _));
        }
    }

    [Fact]
    public void GenerateMtuScanCandidatesProducesDescendingScan()
    {
        var candidates = SmartMtuEngine.MtuCandidates;
        Assert.NotEmpty(candidates);
        Assert.Equal(SmartMtuEngine.MaxMtu, candidates[0]);
        Assert.Equal(SmartMtuEngine.MinMtu, candidates[^1]);

        for (var i = 1; i < candidates.Length; i++)
        {
            Assert.True(candidates[i] < candidates[i - 1]);
            Assert.True(candidates[i] >= SmartMtuEngine.MinMtu);
            Assert.True(candidates[i] <= SmartMtuEngine.MaxMtu);
        }
    }

    [Theory]
    [InlineData("SE", "se")]
    [InlineData("ru", "ru")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void FlagEmojiToCountryCodeConvertsValidCodes(string? input, string? expected)
    {
        var result = FlagHelper.FlagEmojiToCountryCode(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetFlagImageUrlBuildsCdnUrl()
    {
        var url = FlagHelper.GetFlagImageUrl("SE");
        Assert.Equal("https://flagcdn.com/w80/se.png", url);
        Assert.Null(FlagHelper.GetFlagImageUrl(null));
    }
}
