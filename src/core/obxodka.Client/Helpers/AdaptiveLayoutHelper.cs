namespace obxodka.Helpers;

public static class AdaptiveLayoutHelper
{
    public const double WideBreakpointWidth = 700.0;
    public const double MediumBreakpointWidth = 600.0;
    public const double ShortScreenBreakpointHeight = 520.0;

    public static bool IsWideLayout(double width, bool isDesktopOrTablet = false) => width <= 0 ? isDesktopOrTablet : width >= WideBreakpointWidth || (isDesktopOrTablet && width >= MediumBreakpointWidth);

    public static bool IsShortScreen(double height) =>
        height is > 0 and < ShortScreenBreakpointHeight;

    public static (double buttonSize, double graphHeight, double bottomNavReserve) CalculateVpnViewDimensions(
        double width,
        double height,
        double topInset = 0,
        bool isDesktopOrTablet = false)
    {
        var isWide = IsWideLayout(width, isDesktopOrTablet);
        if (isWide)
        {
            return (280.0, -1.0, 0.0);
        }

        var isShort = IsShortScreen(height);
        var topPad = Math.Max(topInset + 4.0, 8.0);
        var bottomNavReserve = isShort ? 20.0 : 120.0;
        var availableH = height - topPad - bottomNavReserve;
        if (availableH <= 0)
        {
            return (130.0, 50.0, bottomNavReserve);
        }

        var fixedOverhead = isShort ? 180.0 : 290.0;
        var minFlex = isShort ? 120.0 : 180.0;
        var flex = Math.Max(minFlex, availableH - fixedOverhead);

        var minButton = isShort ? 130.0 : 160.0;
        var maxButton = isShort ? 180.0 : 240.0;
        var buttonSize = Math.Clamp(Math.Round(flex * 0.58), minButton, maxButton);

        var minGraph = isShort ? 50.0 : 75.0;
        var graphHeight = Math.Clamp(Math.Round(flex - buttonSize - 10.0), minGraph, 180.0);

        if (buttonSize + graphHeight + fixedOverhead > availableH)
        {
            var excess = buttonSize + graphHeight + fixedOverhead - availableH;
            var minG = isShort ? 50.0 : 70.0;
            if (graphHeight - excess >= minG)
            {
                graphHeight -= excess;
            }
            else
            {
                var remainingExcess = excess - (graphHeight - minG);
                graphHeight = minG;
                buttonSize = Math.Max(minButton, buttonSize - remainingExcess);
            }
        }

        return (buttonSize, graphHeight, bottomNavReserve);
    }
}
