namespace obxodka.Helpers;

public static partial class UIAnimations
{
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

}
