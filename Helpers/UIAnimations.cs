namespace obxodka.Helpers;

public static class UIAnimations
{
    private const uint DurFast = 150;
    private const uint DurNormal = 280;
    private const uint DurSlow = 450;
    private const uint DurSpring = 500;

    private static readonly Color t_primaryColor = Color.FromArgb("#7C3AED");
    private static readonly Color t_mutedColor = Color.FromArgb("#6A5A8A");
    private static readonly Color t_cyanColor = Color.FromArgb("#00E5FF");
    private static readonly Color t_disconnectedLabelColor = Color.FromArgb("#C4ABFF");
    private static readonly Color t_sideItemBgActive = Color.FromArgb("#227C3AED");
    private static readonly Color t_sideItemStrokeActive = Color.FromArgb("#557C3AED");

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

        foreach (var el in elements)
        {
            if (el is null)
            {
                continue;
            }

            _ = el.FadeToAsync(1, duration, Easing.CubicOut);
            _ = el.TranslateToAsync(0, 0, duration, Easing.SpringOut);
            _ = el.ScaleToAsync(1.0, duration, Easing.SpringOut);

            if (delayBetweenMs > 0)
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
        incoming.Opacity = 1.0;
        incoming.Scale = 1.0;
        incoming.TranslationY = 0;
        incoming.TranslationX = 0;
        incoming.IsVisible = true;

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

    public static void SetNavActive(MauiIcon? bottomIcon, Border? sideItem)
    {
        if (bottomIcon is not null)
        {
            bottomIcon.CancelAnimations();
            bottomIcon.IconColor = t_primaryColor;
            _ = bottomIcon.ScaleToAsync(1.08, DurNormal, Easing.SpringOut);
        }

        if (sideItem is not null)
        {
            sideItem.CancelAnimations();
            sideItem.BackgroundColor = t_sideItemBgActive;
            sideItem.Stroke = t_sideItemStrokeActive;
            sideItem.StrokeThickness = 1;
            _ = sideItem.ScaleToAsync(1.0, DurNormal, Easing.SpringOut);
        }
    }

    public static void SetNavInactive(MauiIcon? bottomIcon, Border? sideItem)
    {
        if (bottomIcon is not null)
        {
            bottomIcon.CancelAnimations();
            bottomIcon.IconColor = t_mutedColor;
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
            pill.FadeToAsync(1, DurNormal, Easing.SpringOut),
            pill.ScaleToAsync(1.0, DurNormal, Easing.SpringOut)
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

        _ = aura.AbortAnimation("AuraPulse");
        aura.CancelAnimations();
        _ = await aura.FadeToAsync(0, DurNormal, Easing.CubicOut);
        aura.Scale = 1.0;
    }

    public static async Task SetVpnConnectedAsync(
        MauiIcon? icon,
        Label? label,
        VisualElement? aura)
    {
        _ = (icon?.IconColor = t_cyanColor);
        _ = (label?.TextColor = t_cyanColor);

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

    public static async Task SetVpnDisconnectedAsync(
        MauiIcon? icon,
        Label? label,
        VisualElement? aura)
    {
        _ = (icon?.IconColor = t_primaryColor);
        _ = (label?.TextColor = t_disconnectedLabelColor);

        if (aura is not null)
        {
            await StopAuraPulseAsync(aura);
        }
    }

    public static async Task SetButtonLoadingAsync(
        Border? border,
        Button? button,
        ActivityIndicator? indicator,
        bool loading)
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

        _ = await Task.WhenAll(
            sidebar.FadeToAsync(1, DurSpring, Easing.CubicOut),
            sidebar.TranslateToAsync(0, 0, DurSpring, Easing.SpringOut)
        );

        await PlayEntranceCascadeAsync(60, DurNormal, navItems);
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
            bar.FadeToAsync(1, DurSpring, Easing.CubicOut),
            bar.TranslateToAsync(0, 0, DurSpring, Easing.SpringOut)
        );
    }

    #region Universal Icon Micro-Animations

    public enum IconAnimationType
    {
        SpringScale,
        Spin,
        BounceJump,
        Wiggle,
        Twinkle,
        Pulse
    }

    public static async Task PlayIconSpringHoverAsync(this VisualElement? icon, double targetScale = 1.25)
    {
        if (icon is null)
        {
            return;
        }

        _ = await icon.ScaleToAsync(targetScale, DurFast, Easing.SpringOut);
    }

    public static async Task PlayIconHoverExitAsync(this VisualElement? icon)
    {
        if (icon is null)
        {
            return;
        }

        _ = await Task.WhenAll(
            icon.ScaleToAsync(1.0, DurFast, Easing.CubicOut),
            icon.TranslateToAsync(0, 0, DurFast, Easing.CubicOut),
            icon.RotateToAsync(0, DurFast, Easing.CubicOut)
        );
    }

    public static async Task PlayIconSpinAsync(this VisualElement? icon, double degrees = 180, uint duration = DurNormal)
    {
        if (icon is null)
        {
            return;
        }

        _ = icon.ScaleToAsync(1.22, DurFast, Easing.SpringOut);
        _ = await icon.RelRotateToAsync(degrees, duration, Easing.SpringOut);
    }

    public static async Task PlayIconBounceJumpAsync(this VisualElement? icon, double jumpY = -4)
    {
        if (icon is null)
        {
            return;
        }

        _ = icon.TranslateToAsync(0, jumpY, DurFast, Easing.SpringOut);
        _ = await icon.ScaleToAsync(1.2, DurFast, Easing.SpringOut);
    }

    public static async Task PlayIconWiggleAsync(this VisualElement? icon, double angle = 14)
    {
        if (icon is null)
        {
            return;
        }

        _ = await icon.RotateToAsync(angle, 50, Easing.Linear);
        _ = await icon.RotateToAsync(-angle, 50, Easing.Linear);
        _ = await icon.RotateToAsync(0, 50, Easing.CubicOut);
    }

    public static async Task PlayIconTwinkleAsync(this VisualElement? icon)
    {
        if (icon is null)
        {
            return;
        }

        _ = await icon.ScaleToAsync(1.35, 120, Easing.SpringOut);
        _ = icon.ScaleToAsync(1.0, 120, Easing.CubicOut);
    }

    public static async Task PlayIconPulseAsync(this VisualElement? icon, double peakScale = 1.2)
    {
        if (icon is null)
        {
            return;
        }

        _ = await icon.ScaleToAsync(peakScale, 140, Easing.SinInOut);
        _ = icon.ScaleToAsync(1.0, 140, Easing.SinInOut);
    }

    public static void AttachIconHover(this Border item, VisualElement icon, IconAnimationType type)
    {
        var pointer = new PointerGestureRecognizer();
        pointer.PointerEntered += async (_, _) =>
        {
            _ = item.ScaleToAsync(1.04, DurFast, Easing.CubicOut);
            switch (type)
            {
                case IconAnimationType.SpringScale:
                    await icon.PlayIconSpringHoverAsync();
                    break;
                case IconAnimationType.Spin:
                    await icon.PlayIconSpinAsync();
                    break;
                case IconAnimationType.BounceJump:
                    await icon.PlayIconBounceJumpAsync();
                    break;
                case IconAnimationType.Wiggle:
                    await icon.PlayIconWiggleAsync();
                    break;
                case IconAnimationType.Twinkle:
                    await icon.PlayIconTwinkleAsync();
                    break;
                case IconAnimationType.Pulse:
                    await icon.PlayIconPulseAsync();
                    break;
                default:
                    break;
            }
        };
        pointer.PointerExited += async (_, _) =>
        {
            _ = item.ScaleToAsync(1.0, DurFast, Easing.CubicIn);
            await icon.PlayIconHoverExitAsync();
        };
        item.GestureRecognizers.Add(pointer);
    }

    #endregion
}
