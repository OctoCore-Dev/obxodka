namespace obxodka.Client.Tests.Mesh;

public class BandwidthLimiterTests
{
    [Fact]
    public async Task ConsumeAsyncSmallBytesCompletesInstantlyAsync()
    {
        var limiter = new BandwidthLimiter(10);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var valTask = limiter.ConsumeAsync(1024, cts.Token);
        Assert.True(valTask.IsCompletedSuccessfully);
        await valTask;
    }

    [Fact]
    public async Task ConsumeAsyncZeroOrNegativeBytesReturnsImmediatelyAsync()
    {
        var limiter = new BandwidthLimiter(5);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var t0 = limiter.ConsumeAsync(0, cts.Token);
        var tNeg = limiter.ConsumeAsync(-100, cts.Token);

        Assert.True(t0.IsCompletedSuccessfully);
        Assert.True(tNeg.IsCompletedSuccessfully);
        await t0;
        await tNeg;
    }

    [Fact]
    public async Task ConsumeAsyncWhenTokensExhaustedWaitsForRefillAsync()
    {
        var limiter = new BandwidthLimiter(1);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await limiter.ConsumeAsync(250_000, cts.Token);

        var sw = Stopwatch.StartNew();

        await limiter.ConsumeAsync(125_000, cts.Token);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 300, $"Elapsed was {sw.ElapsedMilliseconds} ms, expected delay for token refill.");
    }

    [Fact]
    public async Task UpdateLimitDynamicallyIncreasesCapacityAsync()
    {
        var limiter = new BandwidthLimiter(1);
        limiter.UpdateLimit(50);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var task = limiter.ConsumeAsync(500_000, cts.Token);
        await task;
    }

    [Fact]
    public async Task ConcurrentConsumersMultiThreadedStressTestCompletesSafelyAsync()
    {
        var limiter = new BandwidthLimiter(100);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var tasks = Enumerable.Range(0, 50).Select(async _ =>
        {
            for (var i = 0; i < 20; i++)
            {
                await limiter.ConsumeAsync(1024, cts.Token);
            }
        });

        await Task.WhenAll(tasks);
    }
}
