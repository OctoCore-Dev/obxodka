namespace obxodka.Helpers;

public static partial class UIAnimations
{
    public static async Task BounceClickAsync(this VisualElement? view)
    {
        if (view is null)
        {
            return;
        }

        view.CancelAnimations();
        _ = await view.ScaleToAsync(0.91, 90, Easing.CubicIn);
        _ = await view.ScaleToAsync(1.0, DurNormal, Easing.SpringOut);
    }

    public static Task PressDownAsync(this VisualElement? view)
    {
        view?.CancelAnimations();
        return view?.ScaleToAsync(0.94, 80, Easing.CubicIn) ?? Task.CompletedTask;
    }

    public static Task PressUpAsync(this VisualElement? view) =>
        view?.ScaleToAsync(1.0, DurNormal, Easing.SpringOut) ?? Task.CompletedTask;

    public static async Task ShakeErrorAsync(this VisualElement? element)
    {
        if (element is null)
        {
            return;
        }

        element.CancelAnimations();
        _ = await element.TranslateToAsync(9, 0, 45, Easing.CubicOut);
        _ = await element.TranslateToAsync(-9, 0, 45, Easing.CubicInOut);
        _ = await element.TranslateToAsync(5, 0, 40, Easing.CubicInOut);
        _ = await element.TranslateToAsync(-5, 0, 40, Easing.CubicInOut);
        _ = await element.TranslateToAsync(2, 0, 35, Easing.CubicInOut);
        _ = await element.TranslateToAsync(0, 0, 35, Easing.CubicOut);
    }

    public static async Task FlashErrorAsync(this VisualElement? element)
    {
        if (element is null)
        {
            return;
        }

        element.CancelAnimations();
        for (var i = 0; i < 2; i++)
        {
            _ = await element.FadeToAsync(0.25, 120, Easing.CubicIn);
            _ = await element.FadeToAsync(1.0, 120, Easing.CubicOut);
        }
    }

    public static async Task ShowErrorLabelAsync(VisualElement? label)
    {
        if (label is null)
        {
            return;
        }

        label.CancelAnimations();
        label.TranslationY = -8;
        label.Opacity = 0;
        label.IsVisible = true;

        _ = await Task.WhenAll(
            label.FadeToAsync(1, DurNormal, Easing.CubicOut),
            label.TranslateToAsync(0, 0, DurNormal, Easing.SpringOut)
        );
    }

    public static async Task HideErrorLabelAsync(VisualElement? label)
    {
        if (label is null || label.Opacity == 0)
        {
            return;
        }

        label.CancelAnimations();
        _ = await label.FadeToAsync(0, DurFast, Easing.CubicIn);
    }

    //плавное появление выпадающего списка подсказок доменов
    public static async Task ShowSuggestionsDropdownAsync(VisualElement? dropdown, uint duration = DurFast)
    {
        if (dropdown is null)
        {
            return;
        }

        dropdown.CancelAnimations();
        dropdown.Opacity = 0;
        dropdown.TranslationY = -8;
        dropdown.Scale = 0.98;
        dropdown.IsVisible = true;

        _ = await Task.WhenAll(
            dropdown.FadeToAsync(1, duration, Easing.CubicOut),
            dropdown.TranslateToAsync(0, 0, duration, Easing.CubicOut),
            dropdown.ScaleToAsync(1.0, duration, Easing.CubicOut)
        );
    }

    //плавное скрытие выпадающего списка подсказок доменов
    public static async Task HideSuggestionsDropdownAsync(VisualElement? dropdown, uint duration = 120)
    {
        if (dropdown is null || !dropdown.IsVisible)
        {
            return;
        }

        dropdown.CancelAnimations();
        _ = await Task.WhenAll(
            dropdown.FadeToAsync(0, duration, Easing.CubicIn),
            dropdown.TranslateToAsync(0, -6, duration, Easing.CubicIn),
            dropdown.ScaleToAsync(0.98, duration, Easing.CubicIn)
        );

        if (dropdown.Opacity <= 0.05)
        {
            dropdown.IsVisible = false;
            dropdown.TranslationY = 0;
            dropdown.Scale = 1.0;
        }
    }

    public static void SetNavActive(MauiIcon? bottomIcon, Border? sideItem)
    {
        var primary = GetThemeColor("Primary", s_primaryColor);

        if (bottomIcon is not null)
        {
            bottomIcon.CancelAnimations();
            bottomIcon.IconColor = primary;
            _ = bottomIcon.ScaleToAsync(1.08, DurNormal, Easing.SpringOut);
        }

        if (sideItem is not null)
        {
            sideItem.CancelAnimations();
            sideItem.BackgroundColor = primary.WithAlpha(0.14f);
            sideItem.Stroke = primary.WithAlpha(0.35f);
            sideItem.StrokeThickness = 1;
            _ = sideItem.ScaleToAsync(1.0, DurNormal, Easing.SpringOut);
        }
    }

    public static void SetNavInactive(MauiIcon? bottomIcon, Border? sideItem)
    {
        if (bottomIcon is not null)
        {
            bottomIcon.CancelAnimations();
            bottomIcon.IconColor = GetThemeColor("TextMuted", s_mutedColor);
            _ = bottomIcon.ScaleToAsync(1.0, DurFast, Easing.CubicOut);
        }

        if (sideItem is not null)
        {
            sideItem.CancelAnimations();
            sideItem.BackgroundColor = Colors.Transparent;
            sideItem.Stroke = Colors.Transparent;
            sideItem.StrokeThickness = 0;
        }
    }

    public static async Task ShowPillAsync(BoxView? pill)
    {
        if (pill is null)
        {
            return;
        }

        pill.CancelAnimations();
        pill.Opacity = 0;
        pill.Scale = 0.5;
        pill.IsVisible = true;

        _ = await Task.WhenAll(
            pill.FadeToAsync(1, DurFast, Easing.SpringOut),
            pill.ScaleToAsync(1.0, DurFast, Easing.SpringOut)
        );
    }

    public static async Task HidePillAsync(BoxView? pill)
    {
        if (pill is null || !pill.IsVisible)
        {
            return;
        }

        pill.CancelAnimations();
        _ = await Task.WhenAll(
            pill.FadeToAsync(0, DurFast, Easing.CubicIn),
            pill.ScaleToAsync(0.5, DurFast, Easing.CubicIn)
        );

        pill.IsVisible = false;
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "MAUI Animation is managed and committed to visual element lifecycle")]
    public static void StartAuraPulse(VisualElement? aura)
    {
        if (aura is null)
        {
            return;
        }

        _ = aura.AbortAnimation("AuraPulse");
        aura.CancelAnimations();
        aura.IsVisible = true;

        var parentAnimation = new Animation();
        var maxScale = DeviceInfo.Idiom == DeviceIdiom.Phone ? 1.05 : 1.15;
        var scaleUp = new Animation(v => aura.Scale = v, 1.0, maxScale, Easing.SinInOut);
        var scaleDown = new Animation(v => aura.Scale = v, maxScale, 1.0, Easing.SinInOut);
        var fadeUp = new Animation(v => aura.Opacity = v, 0.65, 0.85, Easing.SinInOut);
        var fadeDown = new Animation(v => aura.Opacity = v, 0.85, 0.65, Easing.SinInOut);

        parentAnimation.Add(0, 0.5, scaleUp);
        parentAnimation.Add(0.5, 1, scaleDown);
        parentAnimation.Add(0, 0.5, fadeUp);
        parentAnimation.Add(0.5, 1, fadeDown);

        parentAnimation.Commit(aura, "AuraPulse", 16, 2500, null, null, () => true);
    }

    public static async Task StopAuraPulseAsync(VisualElement? aura)
    {
        if (aura is null)
        {
            return;
        }

        try
        {
            _ = aura.AbortAnimation("AuraPulse");
            aura.CancelAnimations();
            _ = await aura.FadeToAsync(0, DurNormal, Easing.CubicOut);
            aura.Scale = 1.0;
        }
        catch (Exception)
        {
            try
            { aura.Opacity = 0; aura.Scale = 1.0; }
            catch { }
        }
    }

    public static async Task SetVpnConnectedAsync(
        MauiIcon? icon,
        Label? label,
        VisualElement? aura)
    {
        try
        {
            var accent = GetThemeColor("Accent", s_cyanColor);
            _ = (icon?.IconColor = accent);
            _ = (label?.TextColor = accent);

            if (aura is not null)
            {
                _ = aura.AbortAnimation("AuraPulse");
                aura.CancelAnimations();
                aura.Opacity = 0;
                aura.IsVisible = true;
                _ = await aura.FadeToAsync(0.65, 400, Easing.CubicOut);
                StartAuraPulse(aura);
            }
        }
        catch (Exception)
        {
        }
    }

    public static async Task SetVpnDisconnectedAsync(
        MauiIcon? icon,
        Label? label,
        VisualElement? aura)
    {
        try
        {
            var primary = GetThemeColor("Primary", s_primaryColor);
            var primaryBright = GetThemeColor("PrimaryBright", s_disconnectedLabelColor);

            _ = (icon?.IconColor = primary);
            _ = (label?.TextColor = primaryBright);

            if (aura is not null)
            {
                await StopAuraPulseAsync(aura);
            }
        }
        catch (Exception)
        {
        }
    }

    public static async Task SetButtonLoadingAsync(
        Border? border,
        Button? button,
        ActivityIndicator? indicator,
        bool loading)
    {
        try
        {
            if (loading)
            {
                if (border is not null)
                {
                    border.CancelAnimations();
                    _ = border.ScaleToAsync(0.97, DurFast, Easing.CubicIn);
                }

                if (button is not null)
                {
                    button.CancelAnimations();
                    button.IsEnabled = false;
                    _ = await button.FadeToAsync(0, DurFast, Easing.CubicIn);
                }

                if (indicator is not null)
                {
                    indicator.CancelAnimations();
                    indicator.Opacity = 0;
                    indicator.IsVisible = true;
                    _ = indicator.FadeToAsync(1, DurNormal, Easing.CubicOut);
                }
            }
            else
            {
                if (indicator is not null)
                {
                    indicator.CancelAnimations();
                    _ = await indicator.FadeToAsync(0, DurFast, Easing.CubicIn);
                    indicator.IsVisible = false;
                }

                if (button is not null)
                {
                    button.CancelAnimations();
                    button.IsEnabled = true;
                    _ = button.FadeToAsync(1, DurFast, Easing.CubicOut);
                }

                if (border is not null)
                {
                    border.CancelAnimations();
                    _ = border.ScaleToAsync(1.0, DurSpring, Easing.SpringOut);
                }
            }
        }
        catch (Exception)
        {
        }
    }
}
