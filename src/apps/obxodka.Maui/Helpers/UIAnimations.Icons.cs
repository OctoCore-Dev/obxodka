namespace obxodka.Helpers;

public static partial class UiAnimations
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

    extension(VisualElement? icon)
    {
        internal async Task PlayIconSpringHoverAsync(double targetScale = 1.25)
        {
            if (icon is null)
            {
                return;
            }

            _ = await icon.ScaleToAsync(targetScale, DurFast, Easing.SpringOut);
        }

        internal async Task PlayIconHoverExitAsync()
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

        internal async Task PlayIconSpinAsync(double degrees = 180, uint duration = DurNormal)
        {
            if (icon is null)
            {
                return;
            }

            _ = await Task.WhenAll(
                icon.ScaleToAsync(1.14, duration / 2, Easing.CubicOut),
                icon.RelRotateToAsync(degrees, duration, Easing.CubicOut)
            );
            _ = await icon.ScaleToAsync(1.0, 120, Easing.CubicOut);
        }

        internal async Task PlayIconBounceJumpAsync(double jumpY = -3)
        {
            if (icon is null)
            {
                return;
            }

            _ = await Task.WhenAll(
                icon.TranslateToAsync(0, jumpY, 110, Easing.CubicOut),
                icon.ScaleToAsync(1.14, 110, Easing.CubicOut)
            );
            _ = await Task.WhenAll(
                icon.TranslateToAsync(0, 0, 130, Easing.CubicOut),
                icon.ScaleToAsync(1.0, 130, Easing.CubicOut)
            );
        }

        internal async Task PlayIconWiggleAsync(double angle = 14)
        {
            if (icon is null)
            {
                return;
            }

            _ = await icon.RotateToAsync(angle, 50, Easing.Linear);
            _ = await icon.RotateToAsync(-angle, 50, Easing.Linear);
            _ = await icon.RotateToAsync(0, 50, Easing.CubicOut);
        }

        internal async Task PlayIconTwinkleAsync()
        {
            if (icon is null)
            {
                return;
            }

            _ = await icon.ScaleToAsync(1.35, 120, Easing.SpringOut);
            _ = icon.ScaleToAsync(1.0, 120, Easing.CubicOut);
        }

        internal async Task PlayIconPulseAsync(double peakScale = 1.2)
        {
            if (icon is null)
            {
                return;
            }

            _ = await icon.ScaleToAsync(peakScale, 140, Easing.SinInOut);
            _ = icon.ScaleToAsync(1.0, 140, Easing.SinInOut);
        }
    }

    extension(Border item)
    {
        public void AttachIconHover(VisualElement icon, IconAnimationType type)
        {
            var pointer = new PointerGestureRecognizer();
            pointer.PointerEntered += async (_, _) =>
            {
                _ = item.ScaleToAsync(1.04, DurFast, Easing.CubicOut);
                await (type switch
                {
                    IconAnimationType.SpringScale => icon.PlayIconSpringHoverAsync(),
                    IconAnimationType.Spin => icon.PlayIconSpinAsync(),
                    IconAnimationType.BounceJump => icon.PlayIconBounceJumpAsync(),
                    IconAnimationType.Wiggle => icon.PlayIconWiggleAsync(),
                    IconAnimationType.Twinkle => icon.PlayIconTwinkleAsync(),
                    IconAnimationType.Pulse => icon.PlayIconPulseAsync(),
                    _ => Task.CompletedTask
                });
            };
            pointer.PointerExited += async (_, _) =>
            {
                _ = item.ScaleToAsync(1.0, DurFast, Easing.CubicIn);
                await icon.PlayIconHoverExitAsync();
            };
            item.GestureRecognizers.Add(pointer);
        }
    }
}
