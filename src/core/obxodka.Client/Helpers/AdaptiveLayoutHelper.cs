namespace obxodka.Helpers;

//класс ширины окна по стандартам material 3 adaptive и fluent 2
public enum WindowLayoutClass
{
    Compact = 0,
    Medium = 1,
    Expanded = 2
}

//неизменяемый снимок геометрии окна для единого расчета адаптивной верстки (ssot)
public readonly record struct AdaptiveViewport(
    double WindowWidth,
    double WindowHeight,
    double ContentWidth,
    double SafeTop,
    double SafeBottom,
    WindowLayoutClass LayoutClass,
    bool UseSidebar,
    bool UseOverlaySidebar,
    bool IsShortScreen)
{
    //признак компактного мобильного экрана с нижней навигацией
    public bool IsCompact => LayoutClass == WindowLayoutClass.Compact;

    //признак широкого экрана с двухколоночной сеткой контента
    public bool IsExpanded => LayoutClass == WindowLayoutClass.Expanded;

    //признак компактного альбомного экрана (телефон или узкое окно в горизонтальном положении)
    public bool IsCompactLandscape => IsShortScreen && ContentWidth >= 560.0;

    //использование двухколоночной раскладки на главном экране vpn
    public bool UseTwoColumnVpn => IsExpanded || IsCompactLandscape;

    //использование многоколоночной сетки карточек на вторичных экранах
    public bool UseMultiColumnContent => IsExpanded || IsShortScreen && ContentWidth >= 600.0;

    //расчет симметричного бокового отступа для центрирования fill-контейнера без widthrequest
    public double ComputeHorizontalPadding(double basePadding, double maxContentWidth = 960.0)
    {
        if (ContentWidth <= 0)
        {
            return basePadding;
        }

        var extra = Math.Floor((ContentWidth - maxContentWidth) / 2.0);
        return Math.Max(basePadding, extra);
    }
}

//метрики умного заполнения карточек vpn в вертикальном режиме для телефонов, fold и портретных планшетов
public readonly record struct VerticalVpnLayoutMetrics(
    double ButtonSize,
    double HeroHeight,
    double GraphHeight,
    double StatCardHeight,
    double ContentMinHeight,
    bool ShowGraph);

//единый источник истины адаптивных брейкпоинтов для телефонов, fold/flip, планшетов и windows
public static class AdaptiveLayoutHelper
{
    public const double CompactMaxWidth = 680.0;
    public const double WideContentMinWidth = 860.0;
    public const double ExpandedWindowMinWidth = 960.0;
    public const double OverlaySidebarMaxWindowWidth = 1160.0;
    public const double CollapsedSidebarRailWidth = 88.0;
    private const double ShortScreenBreakpointHeight = 520.0;

    //создание единого снимка адаптивной геометрии окна
    public static AdaptiveViewport CreateViewport(
        double windowWidth,
        double windowHeight,
        double safeTop,
        double safeBottom,
        bool isDesktopOrTablet,
        bool isTablet,
        double dockedSidebarWidth = CollapsedSidebarRailWidth)
    {
        var useSidebar = UseSidebarNavigation(windowWidth, isDesktopOrTablet);
        var useOverlay = useSidebar && ShouldUseOverlaySidebar(windowWidth, isTablet);
        var reservedLeft = useSidebar
            ? (useOverlay ? CollapsedSidebarRailWidth : Math.Max(CollapsedSidebarRailWidth, dockedSidebarWidth))
            : 0.0;
        var contentWidth = windowWidth > 0 ? Math.Max(0.0, windowWidth - reservedLeft) : 0.0;

        var layoutClass = !useSidebar
            ? WindowLayoutClass.Compact
            : (contentWidth > 0 ? contentWidth >= WideContentMinWidth : isDesktopOrTablet && !isTablet)
                ? WindowLayoutClass.Expanded
                : WindowLayoutClass.Medium;

        return new AdaptiveViewport(
            windowWidth,
            windowHeight,
            contentWidth,
            Math.Max(0.0, safeTop),
            Math.Max(0.0, safeBottom),
            layoutClass,
            useSidebar,
            useOverlay,
            IsShortScreen(windowHeight));
    }

    //использование левого бокового сайдбара вместо нижней мобильной панели
    public static bool UseSidebarNavigation(double pageWidth, bool isDesktopOrTablet = false) =>
        pageWidth <= 0 ? isDesktopOrTablet : pageWidth >= CompactMaxWidth;

    //режим наложения раскрытого сайдбара поверх контента (планшеты и узкие окна)
    public static bool ShouldUseOverlaySidebar(double pageWidth, bool isTablet) =>
        isTablet || pageWidth > 0 && pageWidth < OverlaySidebarMaxWindowWidth;

    //двухколоночный макет контента по реальной доступной ширине
    public static bool IsWideLayout(double width, bool isDesktopOrTablet = false) =>
        width <= 0 ? isDesktopOrTablet : width >= WideContentMinWidth;

    //расчет безопасной ширины карточек с ограничением максимума
    public static double GetSafeCardWidth(double availableWidth, double maxWidth = 950.0) =>
        availableWidth <= 6 ? 0.0 : Math.Min(availableWidth - 6.0, maxWidth);

    //короткий экран смартфона
    public static bool IsShortScreen(double height) =>
        height is > 0 and < ShortScreenBreakpointHeight;

    //расчет геометрии кнопки графика и отступов под размер экрана
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

        if (!(buttonSize + graphHeight + fixedOverhead > availableH))
        {
            return (buttonSize, graphHeight, bottomNavReserve);
        }

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

        return (buttonSize, graphHeight, bottomNavReserve);
    }

    //умное распределение высоты кнопки, карточки графика и статистики в вертикальном режиме
    public static VerticalVpnLayoutMetrics CalculateVerticalVpnLayout(
        AdaptiveViewport viewport,
        double topPad,
        double bottomPad)
    {
        if (viewport.IsShortScreen)
        {
            return new VerticalVpnLayoutMetrics(
                ButtonSize: 152.0,
                HeroHeight: -1.0,
                GraphHeight: 0.0,
                StatCardHeight: 68.0,
                ContentMinHeight: -1.0,
                ShowGraph: false);
        }

        var contentWidth = viewport.ContentWidth > 0 ? viewport.ContentWidth : 400.0;
        var windowHeight = viewport.WindowHeight > 0 ? viewport.WindowHeight : 800.0;
        var usableH = Math.Max(420.0, windowHeight - topPad - bottomPad);
        var isTallTablet = usableH >= 860.0;
        var statCardH = isTallTablet ? 88.0 : 68.0;

        //реальная сумма фиксированных блоков + запас чтобы экран не уходил в лишний скролл
        var fixedOtherH = isTallTablet
            ? 58.0 + statCardH + 34.0 + 76.0 + 18.0
            : 56.0 + statCardH + 24.0 + 74.0 + 18.0;
        var budget = Math.Max(290.0, usableH - fixedOtherH);
        var heroShare = isTallTablet ? 0.52 : 0.56;
        var heroHeight = Math.Clamp(Math.Round(budget * heroShare), 255.0, 560.0);

        var maxBtnByWidth = Math.Floor(contentWidth * (contentWidth >= 600.0 ? 0.44 : 0.52));
        var maxBtnByHero = heroHeight - (isTallTablet ? 148.0 : 102.0);
        var buttonSize = Math.Clamp(Math.Min(maxBtnByWidth, maxBtnByHero), 148.0, 290.0);
        var graphHeight = Math.Clamp(Math.Round(budget - heroHeight), 76.0, 440.0);
        var contentMinHeight = windowHeight >= 520.0 ? windowHeight : -1.0;

        return new VerticalVpnLayoutMetrics(
            buttonSize,
            heroHeight,
            graphHeight,
            statCardH,
            contentMinHeight,
            true);
    }

    //умный расчет диаметра кнопки подключения в двухколоночном режиме от реальных габаритов окна
    public static double CalculateWideVpnButtonSize(AdaptiveViewport viewport)
    {
        var contentWidth = viewport.ContentWidth > 0 ? viewport.ContentWidth : 1000.0;
        var windowHeight = viewport.WindowHeight > 0 ? viewport.WindowHeight : 800.0;

        if (viewport.IsCompactLandscape)
        {
            var usableH = Math.Max(260.0, windowHeight - viewport.SafeTop - viewport.SafeBottom - 24.0);
            return Math.Clamp(Math.Round(Math.Min(usableH * 0.48, contentWidth * 0.22)), 136.0, 210.0);
        }

        return Math.Clamp(Math.Round(Math.Min(contentWidth * 0.27, windowHeight * 0.40)), 200.0, 290.0);
    }
}
