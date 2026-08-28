namespace obxodka.Client.Tests.Mesh;

public class MeshStatsTests
{
    [Fact]
    public void InitialState_AllCountersZero()
    {
        var stats = new MeshStats();
        Assert.Equal(0, stats.BytesRelayedTotal);
        Assert.Equal(0, stats.ActiveClients);
        Assert.Equal(0.0, stats.CurrentMbps);
    }

    [Fact]
    public void AddBytes_IncrementsTotalBytes_LockFree()
    {
        var stats = new MeshStats();
        stats.AddBytes(1024);
        stats.AddBytes(2048);

        Assert.Equal(3072, stats.BytesRelayedTotal);
    }

    [Fact]
    public void AddBytes_IgnoresZeroOrNegativeValues()
    {
        var stats = new MeshStats();
        stats.AddBytes(100);
        stats.AddBytes(0);
        stats.AddBytes(-50);

        Assert.Equal(100, stats.BytesRelayedTotal);
    }

    [Fact]
    public void ClientCounters_IncrementAndDecrement_TrackAccurately()
    {
        var stats = new MeshStats();
        stats.IncrementClients();
        stats.IncrementClients();
        stats.IncrementClients();

        Assert.Equal(3, stats.ActiveClients);

        stats.DecrementClients();
        Assert.Equal(2, stats.ActiveClients);
    }

    [Fact]
    public async Task SampleThroughputCalculatesMbpsBasedOnDeltaBytesAsync()
    {
        var stats = new MeshStats();

        stats.AddBytes(1_000_000);

        await Task.Delay(600);
        stats.SampleThroughput();

        Assert.True(stats.CurrentMbps > 0.0, $"Expected Mbps > 0, got {stats.CurrentMbps}");
    }

    [Fact]
    public void Reset_RestoresZeroState()
    {
        var stats = new MeshStats();
        stats.AddBytes(5_000_000);
        stats.IncrementClients();

        stats.Reset();

        Assert.Equal(0, stats.BytesRelayedTotal);
        Assert.Equal(0, stats.ActiveClients);
        Assert.Equal(0.0, stats.CurrentMbps);
    }

    [Fact]
    public async Task MultiThreadedTrafficAdditionThreadSafeAndExactAsync()
    {
        var stats = new MeshStats();
        const int threadCount = 20;
        const int iterations = 1000;
        const long bytesPerIter = 512;

        var tasks = Enumerable.Range(0, threadCount).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                stats.AddBytes(bytesPerIter);
            }
        }));

        await Task.WhenAll(tasks);

        const long expectedTotal = (long)threadCount * iterations * bytesPerIter;
        Assert.Equal(expectedTotal, stats.BytesRelayedTotal);
    }
}
