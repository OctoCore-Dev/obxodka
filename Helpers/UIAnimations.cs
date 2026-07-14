namespace obxodka.Helpers;

public static class UIAnimations
{
    public static async Task PlayEntranceCascadeAsync(int delayBetweenMs = 100, uint duration = 600, params VisualElement[] elements)
    {
        foreach (var el in elements)
        {
            if (el != null)
            {
                el.Opacity = 0;
                el.TranslationY = 30;
                if (el is Border)
                {
                    el.Scale = 0.9;
                }
            }
        }

        await Task.Delay(100);

        foreach (var el in elements)
        {
            if (el != null)
            {
                _ = el.FadeToAsync(1, duration, Easing.CubicOut);
                _ = el.TranslateToAsync(0, 0, duration, Easing.SpringOut);
                if (el is Border)
                {
                    _ = el.ScaleToAsync(1.0, duration, Easing.SpringOut);
                }
                await Task.Delay(delayBetweenMs);
            }
        }
    }

    public static async Task BounceClickAsync(this VisualElement view)
    {
        if (view == null)
        {
            return;
        }

        view.CancelAnimations();
        _ = await view.ScaleToAsync(0.90, 150, Easing.CubicOut);
        _ = await view.ScaleToAsync(1.0, 350, Easing.SpringOut);
    }

    public static async Task ShakeErrorAsync(this VisualElement element)
    {
        if (element == null)
        {
            return;
        }

        for (var i = 0; i < 3; i++)
        {
            _ = await element.TranslateToAsync(15, 0, 40);
            _ = await element.TranslateToAsync(-15, 0, 40);
        }
        _ = await element.TranslateToAsync(0, 0, 40);
    }
}
