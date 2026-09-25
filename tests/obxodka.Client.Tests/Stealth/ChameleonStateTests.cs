using obxodka.Shared.Stealth;
using Xunit;

namespace obxodka.Client.Tests.Stealth;

[Trait("Category", "Unit")]
public class ChameleonStateTests
{
    [Fact]
    public void SameSeedProducesIdenticalSequence()
    {
        var ch1 = new ChameleonState(0x12345678, 0);
        var ch2 = new ChameleonState(0x12345678, 0);

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(ch1.NextMask(), ch2.NextMask());
            Assert.Equal(ch1.NextJitter(48), ch2.NextJitter(48));
            Assert.Equal(ch1.NextSplitPosition(1, 3), ch2.NextSplitPosition(1, 3));
            Assert.Equal(ch1.NextDelayMs(15, 25), ch2.NextDelayMs(15, 25));
            Assert.Equal(ch1.NextPort(10000, 50000), ch2.NextPort(10000, 50000));
            Assert.Equal(ch1.NextHopIntervalSeconds(30, 60), ch2.NextHopIntervalSeconds(30, 60));
        }
    }

    [Fact]
    public void DifferentSeedsProduceDifferentSequences()
    {
        var ch1 = new ChameleonState(0x11111111, 0);
        var ch2 = new ChameleonState(0x22222222, 0);

        Assert.NotEqual(ch1.NextMask(), ch2.NextMask());
    }

    [Fact]
    public void NextJitterRespectsBoundaries()
    {
        var ch = new ChameleonState(0xABCDEF01);

        for (var i = 0; i < 100; i++)
        {
            var jitter = ch.NextJitter(32);
            Assert.InRange(jitter, 0, 32);
        }

        Assert.Equal(0, ch.NextJitter(0));
        Assert.Equal(0, ch.NextJitter(-5));
    }

    [Fact]
    public void NextSplitPositionRespectsBoundaries()
    {
        var ch = new ChameleonState(0x55AA55AA);

        for (var i = 0; i < 100; i++)
        {
            var split = ch.NextSplitPosition(1, 4);
            Assert.InRange(split, 1, 4);
        }

        Assert.Equal(2, ch.NextSplitPosition(2, 2));
    }

    [Fact]
    public void NextDelayMsRespectsBoundaries()
    {
        var ch = new ChameleonState(0xCAFEBABE);

        for (var i = 0; i < 100; i++)
        {
            var delay = ch.NextDelayMs(15, 25);
            Assert.InRange(delay, 15, 25);
        }

        Assert.Equal(20, ch.NextDelayMs(20, 20));
    }

    [Fact]
    public void NextPortAndHopIntervalRespectBoundaries()
    {
        var ch = new ChameleonState(0x98765432);

        for (var i = 0; i < 100; i++)
        {
            var port = ch.NextPort(10000, 50000);
            Assert.InRange(port, 10000, 59999);

            var interval = ch.NextHopIntervalSeconds(30, 60);
            Assert.InRange(interval, 30, 60);
        }
    }

    [Fact]
    public void MutateHeaderOffsetRespectsBoundaries()
    {
        var ch = new ChameleonState(0x1337C0DE);

        for (var i = 0; i < 50; i++)
        {
            var offset = ch.MutateHeaderOffset(4);
            Assert.InRange(offset, 0, 4);
        }

        Assert.Equal(0, ch.MutateHeaderOffset(0));
    }
}
