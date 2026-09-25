namespace obxodka.Client.Tests.UI;

public class AdaptiveBreakpointTests
{
    [Theory]
    [InlineData(380, false, false)]
    [InlineData(412, false, false)]
    [InlineData(650, false, false)]
    [InlineData(700, false, true)]
    [InlineData(768, false, true)]
    [InlineData(840, false, true)]
    [InlineData(1024, false, true)]
    [InlineData(1920, false, true)]
    [InlineData(500, true, false)]
    [InlineData(650, true, true)]
    public void IsWideLayoutReturnsExpectedResult(double width, bool isDesktopOrTablet, bool expectedWide)
    {
        var result = AdaptiveLayoutHelper.IsWideLayout(width, isDesktopOrTablet);
        Assert.Equal(expectedWide, result);
    }

    [Theory]
    [InlineData(360, true)]
    [InlineData(400, true)]
    [InlineData(519, true)]
    [InlineData(520, false)]
    [InlineData(800, false)]
    [InlineData(920, false)]
    public void IsShortScreenIdentifiesConstrainedVerticalSpace(double height, bool expectedShort)
    {
        var result = AdaptiveLayoutHelper.IsShortScreen(height);
        Assert.Equal(expectedShort, result);
    }

    [Fact]
    public void CalculateVpnViewDimensionsOnWideScreenReturnsTwoColumnDimensions()
    {
        var (buttonSize, graphHeight, bottomNavReserve) =
            AdaptiveLayoutHelper.CalculateVpnViewDimensions(800, 850);

        Assert.Equal(280.0, buttonSize);
        Assert.Equal(-1.0, graphHeight);
        Assert.Equal(0.0, bottomNavReserve);
    }

    [Fact]
    public void CalculateVpnViewDimensionsOnNormalPhoneReservesBottomNav()
    {
        var (buttonSize, graphHeight, bottomNavReserve) =
            AdaptiveLayoutHelper.CalculateVpnViewDimensions(390, 844, topInset: 40);

        Assert.True(buttonSize is >= 160.0 and <= 240.0, $"Button size {buttonSize} should be in standard phone range");
        Assert.True(graphHeight is >= 75.0 and <= 180.0, $"Graph height {graphHeight} should be in standard phone range");
        Assert.Equal(120.0, bottomNavReserve);
    }

    [Fact]
    public void CalculateVpnViewDimensionsOnFlipCoverScreenScalesDownGracefully()
    {
        var (buttonSize, graphHeight, bottomNavReserve) =
            AdaptiveLayoutHelper.CalculateVpnViewDimensions(360, 360, topInset: 0);

        Assert.True(buttonSize is >= 130.0 and <= 180.0, $"Cover screen button {buttonSize} should scale down");
        Assert.True(graphHeight is >= 50.0 and <= 100.0, $"Cover screen graph {graphHeight} should scale down");
        Assert.Equal(20.0, bottomNavReserve);
    }
}
