namespace obxodka.Client.Tests.Protocols;

[Trait("Category", "Unit")]
public class DnsAdBlockRuleManagerTests
{
    public DnsAdBlockRuleManagerTests() => DnsAdBlocker.ResetToDefaults();

    [Fact]
    public void ParseRuleLinesParsesMultipleFormatsAndIgnoresComments()
    {
        var sampleLines = new[]
        {
            "# Standard comment",
            "! Adblock comment",
            "   ",
            "doubleclick.net",
            "0.0.0.0 bad-tracker.com",
            "127.0.0.1 malicious-ad.ru",
            "||adservice.google.com^",
            "invalid_domain_without_dot",
            "trailing.dot.com.",
            "UPPERCASE.TRACKER.IO"
        };

        var parsed = DnsAdBlockRuleManager.ParseRuleLines(sampleLines);

        Assert.Contains("doubleclick.net", parsed);
        Assert.Contains("bad-tracker.com", parsed);
        Assert.Contains("malicious-ad.ru", parsed);
        Assert.Contains("adservice.google.com", parsed);
        Assert.Contains("trailing.dot.com", parsed);
        Assert.Contains("uppercase.tracker.io", parsed);
        Assert.DoesNotContain("invalid_domain_without_dot", parsed);
        Assert.DoesNotContain("# Standard comment", parsed);
    }

    [Fact]
    public async Task SaveCacheAndLoadFromCacheRoundtripsSuccessfullyAsync()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"adblock_test_{Guid.NewGuid():N}.txt");
        try
        {
            var initialDomains = new[] { "custom-test-domain1.xyz", "custom-test-domain2.xyz" };
            var saved = await DnsAdBlockRuleManager.SaveCacheAsync(tempFile, initialDomains);
            Assert.True(saved);
            Assert.True(File.Exists(tempFile));

            var loadedCount = await DnsAdBlockRuleManager.LoadFromCacheAsync(tempFile);
            Assert.Equal(2, loadedCount);
            Assert.True(DnsAdBlocker.IsBlocked("custom-test-domain1.xyz"));
            Assert.True(DnsAdBlocker.IsBlocked("sub.custom-test-domain2.xyz"));
            Assert.True(DnsAdBlocker.IsBlocked("doubleclick.net"));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
            DnsAdBlocker.ResetToDefaults();
        }
    }

    private sealed class MockHttpMessageHandler(string responseContent) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseContent)
            };
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task SyncFromRemoteAsyncDownloadsAndAppliesNewRulesAsync()
    {
        var mockContent = """
            # AdGuard Filter Sample
            ||remote-ad-network.com^
            ||remote-tracker.net^
            0.0.0.0 telemetry-server.xyz
            127.0.0.1 analytics-node.ru
            remote-rule-5.com
            remote-rule-6.com
            remote-rule-7.com
            remote-rule-8.com
            remote-rule-9.com
            remote-rule-10.com
            """;

        using var handler = new MockHttpMessageHandler(mockContent);
        using var client = new HttpClient(handler);
        var tempCache = Path.Combine(Path.GetTempPath(), $"adblock_remote_test_{Guid.NewGuid():N}.txt");

        try
        {
            var success = await DnsAdBlockRuleManager.SyncFromRemoteAsync("http://localhost/rules.txt", tempCache, client);
            Assert.True(success);
            Assert.True(File.Exists(tempCache));
            Assert.True(DnsAdBlocker.IsBlocked("remote-ad-network.com"));
            Assert.True(DnsAdBlocker.IsBlocked("sub.telemetry-server.xyz"));
            Assert.True(DnsAdBlocker.IsBlocked("googleadservices.com"));
        }
        finally
        {
            if (File.Exists(tempCache))
            {
                File.Delete(tempCache);
            }
            DnsAdBlocker.ResetToDefaults();
        }
    }
}
