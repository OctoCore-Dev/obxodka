namespace obxodka.Helpers;

public static class CardStackScrollHelper
{
    public const double DefaultTransitionZone = 85.0;
    public const double DefaultMinScale = 0.60;
    public const double DefaultPower = 3.5;

    public static (double Scale, double TranslateY, double Opacity) Calculate(
        double relativeY,
        double viewportHeight,
        double zone = DefaultTransitionZone,
        double minScale = DefaultMinScale,
        double power = DefaultPower)
    {
        if (viewportHeight <= 0)
        {
            return (1.0, 0.0, 1.0);
        }

        var startThreshold = zone;
        var bottomThreshold = viewportHeight - zone;

        if (relativeY >= startThreshold && relativeY <= bottomThreshold)
        {
            return (1.0, 0.0, 1.0);
        }

        var isTop = relativeY < startThreshold;
        var delta = isTop ? startThreshold - relativeY : relativeY - bottomThreshold;
        var progress = Math.Clamp(delta / zone, 0.0, 1.0);
        var easeProgress = Math.Pow(1.0 - progress, power);
        var scale = Math.Max(minScale, minScale + ((1.0 - minScale) * easeProgress));
        var clampedDelta = Math.Min(delta, zone);
        var stackTranslateY = isTop ? clampedDelta * 0.45 : -clampedDelta * 0.45;
        var opacity = Math.Clamp(Math.Pow(1.0 - progress, 1.4), 0.0, 1.0);

        return (scale, stackTranslateY, opacity);
    }

    public static void ApplyToChildren(
        IEnumerable<VisualElement> items,
        double scrollY,
        double viewportHeight,
        double containerOffsetY = 0.0,
        double zone = DefaultTransitionZone,
        double minScale = DefaultMinScale,
        double power = DefaultPower)
    {
        if (viewportHeight <= 0)
        {
            return;
        }

        foreach (var item in items)
        {
            if (item.Height <= 0)
            {
                continue;
            }

            var itemCenterY = containerOffsetY + item.Y + (item.Height / 2.0) - scrollY;
            var (scale, transY, opacity) = Calculate(itemCenterY, viewportHeight, zone, minScale, power);

            if (Math.Abs(item.Scale - scale) > 0.005)
            {
                item.Scale = scale;
            }

            if (Math.Abs(item.TranslationY - transY) > 0.5)
            {
                item.TranslationY = transY;
            }

            if (Math.Abs(item.Opacity - opacity) > 0.01)
            {
                item.Opacity = opacity;
            }
        }
    }

    public static void ApplyToChildren(
        ScrollView scrollView,
        IEnumerable<IView> items,
        double zone = DefaultTransitionZone,
        double minScale = DefaultMinScale,
        double power = DefaultPower)
    {
        if (scrollView.Height <= 0)
        {
            return;
        }

        ApplyToChildren(
            items.OfType<VisualElement>(),
            scrollView.ScrollY,
            scrollView.Height,
            containerOffsetY: 0.0,
            zone: zone,
            minScale: minScale,
            power: power);
    }
}
