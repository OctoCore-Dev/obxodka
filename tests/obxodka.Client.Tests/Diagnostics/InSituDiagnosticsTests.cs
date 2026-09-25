using obxodka.Client.Diagnostics;
using Xunit;

namespace obxodka.Client.Tests.Diagnostics;

[Trait("Category", "Unit")]
public class InSituDiagnosticsTests
{
    [Fact]
    public void AnalyzeRstPacketFlagsInjectedAnomalies()
    {
        var injected = InSituDiagnosticsEngine.AnalyzeRstPacket(observedTtl: 64, ipId: 0, windowSize: 0);
        Assert.True(injected.IsInjectedByTspu);
        Assert.Contains("АНОМАЛИЯ", injected.Details);

        var normal = InSituDiagnosticsEngine.AnalyzeRstPacket(observedTtl: 52, ipId: 0x1234, windowSize: 65535);
        Assert.False(normal.IsInjectedByTspu);
        Assert.Contains("Стандартный", normal.Details);
    }

    [Fact]
    public async Task MapTspuHopDistanceReturnsSafeHopDistanceAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var (hop, details) = await InSituDiagnosticsEngine.MapTspuHopDistanceAsync("127.0.0.1", 1, maxHops: 3, cts.Token);
        Assert.True(hop is -1 or >= 1);
        Assert.NotNull(details);
    }

    [Fact]
    public async Task TestCanaryEchoReturnsComprehensiveReportAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));

        var (passed, summary) = await InSituDiagnosticsEngine.TestCanaryEchoAsync(client, cts.Token);
        Assert.NotNull(summary);
        Assert.Contains("Anycast", summary);
    }
}
