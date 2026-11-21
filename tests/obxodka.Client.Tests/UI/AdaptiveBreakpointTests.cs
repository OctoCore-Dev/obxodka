using obxodka.Helpers;
using Xunit;

namespace obxodka.Client.Tests.UI;

//тесты адаптивной верстки под телефоны, fold/flip, планшеты и windows
public class AdaptiveBreakpointTests
{
    [Theory]
    [InlineData(320, false, false)]
    [InlineData(360, false, false)]
    [InlineData(412, false, false)]
    [InlineData(679, false, false)]
    [InlineData(680, false, true)]
    [InlineData(800, true, true)]
    [InlineData(1280, true, true)]
    [InlineData(0, true, true)]
    [InlineData(0, false, false)]
    public void UseSidebarNavigation_RespectsThresholdAndFallback(double width, bool isDesktopOrTablet, bool expected)
    {
        var result = AdaptiveLayoutHelper.UseSidebarNavigation(width, isDesktopOrTablet);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(800, true, true)]
    [InlineData(1280, true, true)]
    [InlineData(900, false, true)]
    [InlineData(1159, false, true)]
    [InlineData(1160, false, false)]
    [InlineData(1440, false, false)]
    public void ShouldUseOverlaySidebar_OverlayOnTabletsAndNarrowWindows(double width, bool isTablet, bool expected)
    {
        var result = AdaptiveLayoutHelper.ShouldUseOverlaySidebar(width, isTablet);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(360, false, false)]
    [InlineData(679, false, false)]
    [InlineData(712, true, false)]
    [InlineData(800, true, false)]
    [InlineData(859, true, false)]
    [InlineData(860, true, true)]
    [InlineData(1280, true, true)]
    [InlineData(0, true, true)]
    [InlineData(0, false, false)]
    public void IsWideLayout_SingleColumnOnPortraitTablet_TwoColumnsOnLandscape(double width, bool isDesktopOrTablet, bool expected)
    {
        var result = AdaptiveLayoutHelper.IsWideLayout(width, isDesktopOrTablet);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(340, 840, false, false, WindowLayoutClass.Compact, false, false)]
    [InlineData(412, 915, false, false, WindowLayoutClass.Compact, false, false)]
    [InlineData(800, 1280, true, true, WindowLayoutClass.Medium, true, true)]
    [InlineData(1280, 800, true, true, WindowLayoutClass.Expanded, true, true)]
    [InlineData(1024, 720, true, false, WindowLayoutClass.Expanded, true, true)]
    [InlineData(1440, 900, true, false, WindowLayoutClass.Expanded, true, false)]
    public void CreateViewport_ClassifiesAllFormFactorsCorrectly(
        double width,
        double height,
        bool isDesktopOrTablet,
        bool isTablet,
        WindowLayoutClass expectedClass,
        bool expectedSidebar,
        bool expectedOverlay)
    {
        var vp = AdaptiveLayoutHelper.CreateViewport(width, height, 24, 16, isDesktopOrTablet, isTablet);
        Assert.Equal(expectedClass, vp.LayoutClass);
        Assert.Equal(expectedSidebar, vp.UseSidebar);
        Assert.Equal(expectedOverlay, vp.UseOverlaySidebar);
        Assert.True(vp.ContentWidth <= vp.WindowWidth);
        if (expectedSidebar)
        {
            Assert.Equal(width - AdaptiveLayoutHelper.CollapsedSidebarRailWidth, vp.ContentWidth);
        }
        else
        {
            Assert.Equal(width, vp.ContentWidth);
        }
    }

    [Fact]
    public void ComputeHorizontalPadding_NeverOverflowsOnPortraitTabletAndCentersOnWideDesktop()
    {
        var portraitTablet = AdaptiveLayoutHelper.CreateViewport(800, 1280, 24, 16, true, true);
        var portraitPad = portraitTablet.ComputeHorizontalPadding(20, 960);
        Assert.Equal(20, portraitPad);
        Assert.True(portraitTablet.ContentWidth - portraitPad * 2 > 600);

        var wideDesktop = AdaptiveLayoutHelper.CreateViewport(1600, 900, 0, 0, true, false, 88);
        var widePad = wideDesktop.ComputeHorizontalPadding(28, 960);
        var effectiveWidth = wideDesktop.ContentWidth - widePad * 2;
        Assert.InRange(effectiveWidth, 958, 962);
    }

    [Theory]
    [InlineData(360, 640)]
    [InlineData(412, 915)]
    [InlineData(390, 844)]
    [InlineData(712, 1280)]
    [InlineData(844, 390)]
    public void CalculateVpnViewDimensions_PortraitAndCompact_FitsReasonably(double width, double height)
    {
        var (btnSize, graphH, bottomNav) = AdaptiveLayoutHelper.CalculateVpnViewDimensions(width, height, 24, false);
        Assert.InRange(btnSize, 130, 240);
        Assert.InRange(graphH, 50, 180);
        Assert.True(bottomNav >= 20);
    }

    [Fact]
    public void CalculateVpnViewDimensions_WideDesktop_ReturnsDesktopFixedValues()
    {
        var (btnSize, graphH, bottomNav) = AdaptiveLayoutHelper.CalculateVpnViewDimensions(1280, 800, 0, true);
        Assert.Equal(280.0, btnSize);
        Assert.Equal(-1.0, graphH);
        Assert.Equal(0.0, bottomNav);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(6, 0)]
    [InlineData(380, 374)]
    [InlineData(700, 694)]
    [InlineData(1200, 950)]
    public void GetSafeCardWidth_ClampsToAvailableAndMax(double available, double expected)
    {
        Assert.Equal(expected, AdaptiveLayoutHelper.GetSafeCardWidth(available));
    }

    [Fact]
    public void CalculateVerticalVpnLayout_PortraitTablet_FillsTallViewportWithoutEmptyGap()
    {
        var viewport = AdaptiveLayoutHelper.CreateViewport(800, 1280, 24, 20, isDesktopOrTablet: true, isTablet: true);
        var metrics = AdaptiveLayoutHelper.CalculateVerticalVpnLayout(viewport, topPad: 24, bottomPad: 20);

        Assert.True(metrics.ShowGraph);
        Assert.InRange(metrics.ButtonSize, 260, 296);
        Assert.InRange(metrics.HeroHeight, 480, 560);
        Assert.InRange(metrics.GraphHeight, 400, 480);
        Assert.Equal(88.0, metrics.StatCardHeight);
        Assert.Equal(1280.0, metrics.ContentMinHeight);
    }

    [Fact]
    public void CalculateVerticalVpnLayout_PhoneAndShortScreen_FitsSafely()
    {
        var phone = AdaptiveLayoutHelper.CreateViewport(412, 915, 36, 16, isDesktopOrTablet: false, isTablet: false);
        var phoneMetrics = AdaptiveLayoutHelper.CalculateVerticalVpnLayout(phone, topPad: 42, bottomPad: 110);

        Assert.True(phoneMetrics.ShowGraph);
        Assert.InRange(phoneMetrics.ButtonSize, 165, 220);
        Assert.InRange(phoneMetrics.GraphHeight, 180, 260);
        Assert.Equal(68.0, phoneMetrics.StatCardHeight);

        var shortLandscape = AdaptiveLayoutHelper.CreateViewport(844, 390, 0, 16, isDesktopOrTablet: false, isTablet: false);
        var shortMetrics = AdaptiveLayoutHelper.CalculateVerticalVpnLayout(shortLandscape, topPad: 12, bottomPad: 96);

        Assert.False(shortMetrics.ShowGraph);
        Assert.Equal(0.0, shortMetrics.GraphHeight);
        Assert.Equal(68.0, shortMetrics.StatCardHeight);
        Assert.True(shortLandscape.IsCompactLandscape);
        Assert.True(shortLandscape.UseTwoColumnVpn);
        Assert.True(shortLandscape.UseMultiColumnContent);
        Assert.InRange(AdaptiveLayoutHelper.CalculateWideVpnButtonSize(shortLandscape), 145, 190);
    }

    [Fact]
    public void CalculateWideVpnButtonSize_ScalesWithWindowBounds()
    {
        var tabletLandscape = AdaptiveLayoutHelper.CreateViewport(1280, 800, 24, 16, isDesktopOrTablet: true, isTablet: true);
        var compactDesktop = AdaptiveLayoutHelper.CreateViewport(1024, 620, 0, 0, isDesktopOrTablet: true, isTablet: false, dockedSidebarWidth: 88);

        Assert.InRange(AdaptiveLayoutHelper.CalculateWideVpnButtonSize(tabletLandscape), 260, 290);
        Assert.InRange(AdaptiveLayoutHelper.CalculateWideVpnButtonSize(compactDesktop), 220, 260);
    }
}
