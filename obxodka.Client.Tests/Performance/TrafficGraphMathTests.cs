namespace obxodka.Client.Tests;

[Trait("Category", "Unit")]
public class TrafficGraphMathTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(500, "500 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(15728640, "15 MB")]
    [InlineData(1073741824, "1 GB")]
    [InlineData(5368709120, "5 GB")]
    public void FormatBytesFormatsProperUnits(double bytes, string expected)
    {
        var result = VpnView.FormatBytes(bytes);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ExponentialSmoothingConvergesTowardsTarget()
    {
        double current = 0;
        double target = 1000;
        var factor = 0.16;

        for (var i = 0; i < 30; i++)
        {
            current += (target - current) * factor;
        }

        Assert.InRange(current, 990.0, 1000.0);
    }
}
