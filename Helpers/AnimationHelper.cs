namespace obxodka.Helpers;
internal static class AnimationHelper
{
    public static async Task EntranceFadeSlideAsync(this VisualElement element, uint duration = 600, uint delay = 0)
    {
        if (element == null) return;
        element.Opacity = 0;
        element.TranslationY = 40;
        if (delay > 0)
            await Task.Delay((int)delay).ConfigureAwait(true);
        await Task.WhenAll(
            element.FadeToAsync(1, duration, Easing.CubicOut),
            element.TranslateToAsync(0, 0, duration, Easing.CubicOut)
        ).ConfigureAwait(true);
    }
    public static async Task BounceClickAsync(this VisualElement view)
    {
        if (view == null) return;
        await view.ScaleToAsync(0.92, 100, Easing.CubicOut).ConfigureAwait(true);
        await view.ScaleToAsync(1.0, 150, Easing.SpringOut).ConfigureAwait(true);
    }
    public static async Task ShakeErrorAsync(this VisualElement element)
    {
        if (element == null) return;
        const uint duration = 50;
        const int offset = 10;
        for (int i = 0; i < 2; i++)
        {
            await element.TranslateToAsync(offset, 0, duration).ConfigureAwait(true);
            await element.TranslateToAsync(-offset, 0, duration).ConfigureAwait(true);
        }
        await element.TranslateToAsync(0, 0, duration).ConfigureAwait(true);
    }
    public static async Task PulseGlowAsync(this VisualElement element, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (element == null) break;
            await element.ScaleToAsync(1.03, 1000, Easing.SinInOut).ConfigureAwait(true);
            if (token.IsCancellationRequested) break;
            await element.ScaleToAsync(1.0, 1000, Easing.SinInOut).ConfigureAwait(true);
        }
    }
}