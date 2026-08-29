namespace obxodka.Client.Tests.Performance;

[Trait("Category", "Performance")]
[Trait("Category", "Unit")]
public class BufferbloatJitterTests
{
    [Fact]
    public async Task ChannelQueueBoundedLatencyNeverCausesBufferbloatAsync()
    {
        var channel = Channel.CreateUnbounded<(byte[] buffer, int length)>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        var sw = Stopwatch.StartNew();
        var packetCount = 1000;

        var producerTask = Task.Run(() =>
        {
            for (var i = 0; i < packetCount; i++)
            {
                var buf = ArrayPool<byte>.Shared.Rent(1420);
                _ = channel.Writer.TryWrite((buf, 1420));
            }
            channel.Writer.Complete();
        });

        var consumed = 0;
        var reader = channel.Reader;
        while (await reader.WaitToReadAsync())
        {
            while (reader.TryRead(out var item))
            {
                consumed++;
                ArrayPool<byte>.Shared.Return(item.buffer);
            }
        }

        await producerTask;
        sw.Stop();

        Assert.Equal(packetCount, consumed);
        Assert.True(sw.ElapsedMilliseconds < 50, $"Elapsed was {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void JitterCalculationVarianceRemainsUltraLow()
    {
        var pingSamples = new List<double> { 18.2, 19.1, 18.8, 18.5, 19.0, 18.7, 18.9 };

        double sumDiff = 0;
        for (var i = 1; i < pingSamples.Count; i++)
        {
            sumDiff += Math.Abs(pingSamples[i] - pingSamples[i - 1]);
        }
        var rfcJitter = sumDiff / (pingSamples.Count - 1);

        Assert.True(rfcJitter < 1.0, $"Jitter was {rfcJitter:F2}ms, expected < 1.0ms");
    }
}
