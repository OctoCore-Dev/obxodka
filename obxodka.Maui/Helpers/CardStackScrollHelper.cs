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

        var topThreshold = zone;
        var bottomThreshold = viewportHeight - zone;

        if (relativeY >= topThreshold && relativeY <= bottomThreshold)
        {
            return (1.0, 0.0, 1.0);
        }

        if (relativeY < topThreshold)
        {
            var progress = Math.Clamp((topThreshold - relativeY) / zone, 0.0, 1.0);
            var easeProgress = Math.Pow(1.0 - progress, power);
            var scale = minScale + ((1.0 - minScale) * easeProgress);
            var stackTranslateY = (topThreshold - relativeY) * 0.45;
            var opacity = Math.Pow(1.0 - progress, 1.4);

            return (Math.Max(minScale, scale), stackTranslateY, Math.Clamp(opacity, 0.0, 1.0));
        }
        else
        {
            var progress = Math.Clamp((relativeY - bottomThreshold) / zone, 0.0, 1.0);
            var easeProgress = Math.Pow(1.0 - progress, power);
            var scale = minScale + ((1.0 - minScale) * easeProgress);
            var stackTranslateY = -(relativeY - bottomThreshold) * 0.45;
            var opacity = Math.Pow(1.0 - progress, 1.4);

            return (Math.Max(minScale, scale), stackTranslateY, Math.Clamp(opacity, 0.0, 1.0));
        }
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

#if ANDROID
    public sealed class CardStackRecyclerViewScrollListener(
        float dpZone = (float)DefaultTransitionZone,
        float minScale = (float)DefaultMinScale,
        float power = (float)DefaultPower) : AndroidX.RecyclerView.Widget.RecyclerView.OnScrollListener
    {
        private readonly float _dpZone = dpZone;
        private readonly float _minScale = minScale;
        private readonly float _power = power;

        public override void OnScrolled(AndroidX.RecyclerView.Widget.RecyclerView recyclerView, int dx, int dy)
        {
            base.OnScrolled(recyclerView, dx, dy);
            ApplyTransformations(recyclerView);
        }

        public void ApplyTransformations(AndroidX.RecyclerView.Widget.RecyclerView recyclerView)
        {
            var childCount = recyclerView.ChildCount;
            if (childCount == 0)
            {
                return;
            }

            var density = recyclerView.Resources?.DisplayMetrics?.Density ?? 1.0f;
            var zone = _dpZone * density;
            var viewportHeight = recyclerView.Height;
            if (viewportHeight <= 0)
            {
                return;
            }

            var topThreshold = zone;
            var bottomThreshold = viewportHeight - zone;

            for (var i = 0; i < childCount; i++)
            {
                var child = recyclerView.GetChildAt(i);
                if (child == null)
                {
                    continue;
                }

                var itemCenterY = child.Top + (child.Height / 2.0f);

                if (itemCenterY >= topThreshold && itemCenterY <= bottomThreshold)
                {
                    if (child.ScaleX != 1.0f)
                    {
                        child.ScaleX = 1.0f;
                        child.ScaleY = 1.0f;
                    }

                    if (child.TranslationY != 0f)
                    {
                        child.TranslationY = 0f;
                    }

                    if (child.Alpha != 1.0f)
                    {
                        child.Alpha = 1.0f;
                    }
                }
                else if (itemCenterY < topThreshold)
                {
                    var progress = Math.Clamp((topThreshold - itemCenterY) / zone, 0f, 1f);
                    var easeProgress = (float)Math.Pow(1.0f - progress, _power);
                    var scale = _minScale + ((1.0f - _minScale) * easeProgress);
                    var stackTranslateY = (topThreshold - itemCenterY) * 0.45f;
                    var opacity = (float)Math.Pow(1.0f - progress, 1.4f);
                    var targetScale = Math.Max(_minScale, scale);

                    child.ScaleX = targetScale;
                    child.ScaleY = targetScale;
                    child.TranslationY = stackTranslateY;
                    child.Alpha = Math.Clamp(opacity, 0f, 1f);
                }
                else
                {
                    var progress = Math.Clamp((itemCenterY - bottomThreshold) / zone, 0f, 1f);
                    var easeProgress = (float)Math.Pow(1.0f - progress, _power);
                    var scale = _minScale + ((1.0f - _minScale) * easeProgress);
                    var stackTranslateY = -(itemCenterY - bottomThreshold) * 0.45f;
                    var opacity = (float)Math.Pow(1.0f - progress, 1.4f);
                    var targetScale = Math.Max(_minScale, scale);

                    child.ScaleX = targetScale;
                    child.ScaleY = targetScale;
                    child.TranslationY = stackTranslateY;
                    child.Alpha = Math.Clamp(opacity, 0f, 1f);
                }
            }
        }
    }
#endif
}
