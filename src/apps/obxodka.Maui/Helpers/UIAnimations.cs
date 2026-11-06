namespace obxodka.Helpers;

public static partial class UIAnimations
{
    private const uint DurFast = 150;
    private const uint DurNormal = 280;
    private const uint DurSlow = 450;
    private const uint DurSpring = 500;

    private static readonly Color s_primaryColor = Color.FromArgb("#7C3AED");
    private static readonly Color s_mutedColor = Color.FromArgb("#6A5A8A");
    private static readonly Color s_cyanColor = Color.FromArgb("#00E5FF");
    private static readonly Color s_disconnectedLabelColor = Color.FromArgb("#C4ABFF");

    private static Color GetThemeColor(string key, Color fallback)
    {
        if (Application.Current?.Resources is { } res)
        {
            if (res.TryGetValue(key, out var val) && val is Color c)
            {
                return c;
            }

            if (res.MergedDictionaries != null)
            {
                foreach (var md in res.MergedDictionaries)
                {
                    if (md.TryGetValue(key, out var mVal) && mVal is Color mc)
                    {
                        return mc;
                    }
                }
            }
        }
        return fallback;
    }

    public static async Task PlayEntranceCascadeAsync(
        int delayBetweenMs = 80,
        uint duration = DurSlow,
        params VisualElement[] elements)
    {
        foreach (var el in elements)
        {
            if (el is null)
            {
                continue;
            }

            el.CancelAnimations();
            el.Opacity = 0;
            el.TranslationY = 32;
            el.Scale = 0.96;
        }

        await Task.Delay(40);

        var count = 0;
        foreach (var el in elements)
        {
            if (el is null)
            {
                continue;
            }

            _ = el.FadeToAsync(1, duration, Easing.CubicOut);
            _ = el.TranslateToAsync(0, 0, duration, Easing.SpringOut);
            _ = el.ScaleToAsync(1.0, duration, Easing.SpringOut);

            count++;
            if (delayBetweenMs > 0 && count < 8)
            {
                await Task.Delay(delayBetweenMs);
            }
        }
    }

    public static async Task PlayEntranceSlideUpAsync(
        VisualElement? element,
        double fromY = 32,
        uint duration = DurNormal)
    {
        if (element is null)
        {
            return;
        }

        element.CancelAnimations();
        element.Opacity = 0;
        element.TranslationY = fromY;
        element.Scale = 0.97;

        _ = await Task.WhenAll(
            element.FadeToAsync(1, duration, Easing.CubicOut),
            element.TranslateToAsync(0, 0, duration, Easing.SpringOut),
            element.ScaleToAsync(1.0, duration, Easing.SpringOut)
        );
    }

    public static async Task PlayEntranceFadeScaleAsync(
        VisualElement? element,
        uint duration = DurNormal)
    {
        if (element is null)
        {
            return;
        }

        element.CancelAnimations();
        element.Opacity = 0;
        element.Scale = 0.82;

        _ = await Task.WhenAll(
            element.FadeToAsync(1, duration, Easing.CubicOut),
            element.ScaleToAsync(1.0, duration, Easing.SpringOut)
        );
    }

    public static async Task PlayEntranceSlideInLeftAsync(
        VisualElement? element,
        double fromX = -36,
        uint duration = DurSpring)
    {
        if (element is null)
        {
            return;
        }

        element.CancelAnimations();
        element.Opacity = 0;
        element.TranslationX = fromX;

        _ = await Task.WhenAll(
            element.FadeToAsync(1, duration, Easing.CubicOut),
            element.TranslateToAsync(0, 0, duration, Easing.SpringOut)
        );
    }

    public static async Task PlayExitFadeScaleAsync(VisualElement? element, uint duration = DurFast)
    {
        if (element is null || !element.IsVisible)
        {
            return;
        }

        element.CancelAnimations();
        _ = await Task.WhenAll(
            element.FadeToAsync(0, duration, Easing.CubicIn),
            element.ScaleToAsync(0.96, duration, Easing.CubicIn)
        );

        element.IsVisible = false;
        element.Scale = 1.0;
    }

    public static async Task PlayExitSlideDownAsync(VisualElement? element, uint duration = DurFast)
    {
        if (element is null || !element.IsVisible)
        {
            return;
        }

        element.CancelAnimations();
        _ = await Task.WhenAll(
            element.FadeToAsync(0, duration, Easing.CubicIn),
            element.TranslateToAsync(0, 20, duration, Easing.CubicIn)
        );

        element.IsVisible = false;
        element.TranslationY = 0;
    }

    public static async Task SwitchViewAsync(VisualElement? outgoing, VisualElement? incoming)
    {
        if (outgoing == incoming)
        {
            if (incoming is not null)
            {
                incoming.IsVisible = true;
                incoming.Opacity = 1.0;
                incoming.Scale = 1.0;
                incoming.TranslationY = 0;
                incoming.TranslationX = 0;
            }
            return;
        }

        if (outgoing is { IsVisible: true })
        {
            outgoing.CancelAnimations();
            outgoing.IsVisible = false;
            outgoing.Opacity = 0;
            outgoing.Scale = 1.0;
        }

        if (incoming is null)
        {
            return;
        }

        incoming.CancelAnimations();
        incoming.Scale = 1.0;
        incoming.TranslationY = 0;
        incoming.TranslationX = 0;
        incoming.Opacity = 0;
        incoming.IsVisible = true;
        _ = incoming.FadeToAsync(1.0, 160, Easing.CubicOut);

        await Task.CompletedTask;
    }

    public static async Task CrossFadeFormAsync(VisualElement? outgoing, VisualElement? incoming)
    {
        if (outgoing is { IsVisible: true })
        {
            outgoing.CancelAnimations();
            _ = await Task.WhenAll(
                outgoing.FadeToAsync(0, 160, Easing.CubicIn),
                outgoing.TranslateToAsync(0, -14, 160, Easing.CubicIn)
            );
            outgoing.IsVisible = false;
            outgoing.TranslationY = 0;
        }

        if (incoming is null)
        {
            return;
        }

        incoming.CancelAnimations();
        incoming.TranslationY = 18;
        incoming.Opacity = 0;
        incoming.IsVisible = true;

        _ = await Task.WhenAll(
            incoming.FadeToAsync(1, 220, Easing.CubicOut),
            incoming.TranslateToAsync(0, 0, 220, Easing.SpringOut)
        );
    }

    public static async Task PlaySidebarEntranceAsync(
            Border? sidebar,
            params VisualElement[] navItems)
    {
        if (sidebar is null)
        {
            return;
        }

        sidebar.CancelAnimations();
        sidebar.Opacity = 0;
        sidebar.TranslationX = -36;
        sidebar.IsVisible = true;

        var sidebarEntrance = Task.WhenAll(
            sidebar.FadeToAsync(1, 240, Easing.CubicOut),
            sidebar.TranslateToAsync(0, 0, 240, Easing.SpringOut)
        );

        var navEntrance = PlayEntranceCascadeAsync(22, 220, navItems);

        await Task.WhenAll(sidebarEntrance, navEntrance);
    }

    public static async Task PlayBottomBarEntranceAsync(Border? bar)
    {
        if (bar is null)
        {
            return;
        }

        bar.CancelAnimations();
        bar.TranslationY = 80;
        bar.Opacity = 0;
        bar.IsVisible = true;

        _ = await Task.WhenAll(
            bar.FadeToAsync(1, 240, Easing.CubicOut),
            bar.TranslateToAsync(0, 0, 240, Easing.SpringOut)
        );
    }

}
