namespace obxodka.Client.Tests.Config;

[Trait("Category", "Unit")]
public class NetworkDefaultsTests
{
    [Fact]
    public void NetworkDefaultsConstantsAreValid()
    {
        Assert.Equal(1360, NetworkDefaults.DefaultMtu);
        Assert.Equal(1280, NetworkDefaults.MinMtu);
        Assert.Equal(1420, NetworkDefaults.MaxMtu);
        Assert.Equal("1.1.1.1", NetworkDefaults.PrimaryDns);
        Assert.Equal("1.0.0.1", NetworkDefaults.SecondaryDns);
        Assert.NotEmpty(NetworkDefaults.TrustedDnsServers);
        Assert.Contains("1.1.1.1", NetworkDefaults.TrustedDnsServers);
        Assert.Contains("8.8.8.8", NetworkDefaults.TrustedDnsServers);
    }

    [Theory]
    [InlineData(0, 1280)]
    [InlineData(-100, 1280)]
    [InlineData(576, 1280)]
    [InlineData(1000, 1280)]
    [InlineData(1279, 1280)]
    [InlineData(1280, 1280)]
    [InlineData(1350, 1350)]
    [InlineData(1360, 1360)]
    [InlineData(1400, 1400)]
    [InlineData(1420, 1420)]
    [InlineData(1421, 1420)]
    [InlineData(1500, 1420)]
    [InlineData(9000, 1420)]
    public void ClampMtuClampsCorrectly(int input, int expected)
    {
        var result = NetworkDefaults.ClampMtu(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void TrustedDnsServersAllParseAsValidIpAddresses()
    {
        foreach (var ip in NetworkDefaults.TrustedDnsServers)
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
        var servers = await NetworkDefaults.GetHealthyDnsServersAsync(timeoutMs: 300, ct: cts.Token);
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
        var candidates = NetworkDefaults.MtuCandidates;
        Assert.NotEmpty(candidates);
        Assert.Equal(NetworkDefaults.MaxMtu, candidates[0]);
        Assert.Equal(NetworkDefaults.MinMtu, candidates[^1]);

        for (var i = 1; i < candidates.Length; i++)
        {
            Assert.True(candidates[i] < candidates[i - 1]);
            Assert.True(candidates[i] >= NetworkDefaults.MinMtu);
            Assert.True(candidates[i] <= NetworkDefaults.MaxMtu);
        }
    }
}
