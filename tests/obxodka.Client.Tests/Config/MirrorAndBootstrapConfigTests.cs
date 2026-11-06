namespace obxodka.Client.Tests.Config;

[Trait("Category", "Unit")]
public sealed class MirrorAndBootstrapConfigTests
{
    [Fact]
    public void BootstrapServersAndMirrorsAreNotNull()
    {
        Assert.NotNull(BootstrapConfig.Servers);
        Assert.NotNull(BootstrapConfig.Mirrors);
        Assert.NotNull(AppConfig.BootstrapServers);
        Assert.NotNull(AppConfig.MirrorDomains);
    }

    [Fact]
    public void MirrorDomainPicksFirstMirrorWhenAvailable()
    {
        var mirrors = MirrorConfig.MirrorDomains;
        if (mirrors.Length > 0)
        {
            Assert.Equal(mirrors[0], MirrorConfig.MirrorDomain);
            Assert.Equal(mirrors[0], AppConfig.MirrorDomain);
        }
        else
        {
            Assert.Equal(AppConfig.PrimaryDomain, MirrorConfig.MirrorDomain);
        }
    }

    [Fact]
    public void DiscoveryUrlsAreValidHttpsAndTargetMainBranch()
    {
        foreach (var url in MirrorConfig.RemoteServersDiscoveryUrls)
        {
            Assert.StartsWith("https://", url);
            Assert.True(Uri.TryCreate(url, UriKind.Absolute, out _));
            Assert.DoesNotContain("develop", url, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void KnownDomainBasesIncludesMirrorsAndDirectApi()
    {
        var known = MirrorConfig.KnownDomainBases;
        Assert.NotEmpty(known);
        Assert.Contains(AppConfig.DirectApiBaseUrl, known, StringComparer.OrdinalIgnoreCase);

        foreach (var mirror in MirrorConfig.MirrorDomains)
        {
            var expectedBase = $"https://{mirror.TrimEnd('/')}/";
            Assert.Contains(expectedBase, known, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DirectServerIpAndRelayIpsAreValid()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppConfig.DirectServerIp));
        Assert.Contains(AppConfig.DirectServerIp, AppConfig.DirectRelayIps);
    }

    [Fact]
    public void DiagnosticEndpointsAreValidHttpsUrls()
    {
        foreach (var ping in DiagnosticEndpoints.PingEndpoints)
        {
            Assert.StartsWith("https://", ping);
            Assert.True(Uri.TryCreate(ping, UriKind.Absolute, out _));
        }

        foreach (var download in DiagnosticEndpoints.SpeedTestDownloadCandidates)
        {
            Assert.StartsWith("https://", download);
            Assert.True(Uri.TryCreate(download, UriKind.Absolute, out _));
        }
    }

    [Fact]
    public void DnsDefaultsBuildDohQueryUrlFormatsCorrectly()
    {
        var url = DnsDefaults.BuildDohQueryUrl("https://1.1.1.1/dns-query", "test.example.com");
        Assert.Equal("https://1.1.1.1/dns-query?name=test.example.com&type=A", url);

        var urlWithParam = DnsDefaults.BuildDohQueryUrl("https://example.com/resolve?auth=1", "test.example.com");
        Assert.Equal("https://example.com/resolve?auth=1&name=test.example.com&type=A", urlWithParam);
    }
}
