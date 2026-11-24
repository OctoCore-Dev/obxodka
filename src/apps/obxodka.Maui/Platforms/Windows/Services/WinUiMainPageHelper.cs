using System.Numerics;
using Windows.Media.Playback;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Rect = Windows.Foundation.Rect;
using RectangleGeometry = Microsoft.UI.Xaml.Media.RectangleGeometry;
using Visibility = Microsoft.UI.Xaml.Visibility;

namespace obxodka.Platforms.Windows;

//нативные winui 3 операции для главной страницы (clip, hit-test, pointer, видеоплеер)
internal static class WinUiMainPageHelper
{
    public static void DisableHitTest(VisualElement element)
    {
        if (element.Handler?.PlatformView is UIElement el)
        {
            el.IsHitTestVisible = false;
        }
    }

    public static void AttachPointerHandlers(
        VisualElement element,
        Action<double, double> onMoved,
        Action<double, double> onPressed,
        Action<double, double> onExited)
    {
        if (element.Handler?.PlatformView is not UIElement el)
        {
            return;
        }

        el.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler((_, args) =>
            {
                var pt = args.GetCurrentPoint(el);
                onMoved(pt.Position.X, pt.Position.Y);
            }),
            true);

        el.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler((_, args) =>
            {
                var pt = args.GetCurrentPoint(el);
                onPressed(pt.Position.X, pt.Position.Y);
            }),
            true);

        el.AddHandler(
            UIElement.PointerExitedEvent,
            new PointerEventHandler((_, args) =>
            {
                var pt = args.GetCurrentPoint(el);
                onExited(pt.Position.X, pt.Position.Y);
            }),
            true);
    }

    public static void ApplyClip(VisualElement element)
    {
        if (element.Handler?.PlatformView is not FrameworkElement fe)
        {
            return;
        }

        UpdateFrameworkElementClip(fe);
        fe.SizeChanged -= OnNativeElementSizeChangedForClip;
        fe.SizeChanged += OnNativeElementSizeChangedForClip;
    }

    public static void ConfigureVideoPlayer(VisualElement videoElement)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return;
        }

        try
        {
            if (videoElement.Handler?.PlatformView is not MediaPlayerElement winPlayer)
            {
                return;
            }

            winPlayer.AreTransportControlsEnabled = false;
            winPlayer.IsHitTestVisible = false;
            if (winPlayer.TransportControls is not null)
            {
                winPlayer.TransportControls.Visibility = Visibility.Collapsed;
                winPlayer.TransportControls.IsEnabled = false;
                winPlayer.TransportControls.IsHitTestVisible = false;
            }

            if (winPlayer.MediaPlayer is null)
            {
                return;
            }

            winPlayer.MediaPlayer.IsMuted = true;
            winPlayer.MediaPlayer.IsLoopingEnabled = true;
            winPlayer.MediaPlayer.AudioCategory = MediaPlayerAudioCategory.Media;
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[MainPage] Error configuring native video player: {ex.Message}");
        }
    }

    public static void StopVideoPlayer(VisualElement videoElement)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) ||
            videoElement.Handler?.PlatformView is not MediaPlayerElement winPlayer)
        {
            return;
        }

        try
        {
            if (winPlayer.MediaPlayer is { } mp)
            {
                mp.Pause();
                var oldSource = mp.Source as IDisposable;
                mp.Source = null;
                oldSource?.Dispose();
            }

            winPlayer.SetMediaPlayer(null);
        }
        catch
        {
            //игнорируем ошибки остановки плеера при выгрузке
        }
    }

    //аппаратная gpu-анимация ширины элемента через composition clip без пересчета layout на каждом кадре
    public static bool AnimateWidthClip(
        VisualElement element,
        double fromWidth,
        double toWidth,
        double cornerRadius,
        int durationMs,
        Action? onCompleted = null)
    {
        if (element.Handler?.PlatformView is not FrameworkElement fe)
        {
            return false;
        }

        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(fe);
            var compositor = visual.Compositor;
            var height = (float)Math.Max(fe.ActualHeight, element.Height > 0 ? element.Height : 600.0);

            var geometry = compositor.CreateRoundedRectangleGeometry();
            geometry.Offset = Vector2.Zero;
            geometry.CornerRadius = new Vector2((float)cornerRadius, (float)cornerRadius);
            geometry.Size = new Vector2((float)fromWidth, height);

            var clip = compositor.CreateGeometricClip(geometry);
            visual.Clip = clip;

            var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.0f, 0.0f), new Vector2(0.0f, 1.0f));
            var sizeAnim = compositor.CreateVector2KeyFrameAnimation();
            sizeAnim.Duration = TimeSpan.FromMilliseconds(durationMs);
            sizeAnim.InsertKeyFrame(0f, new Vector2((float)fromWidth, height));
            sizeAnim.InsertKeyFrame(1f, new Vector2((float)toWidth, height), easing);

            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            geometry.StartAnimation(nameof(CompositionRoundedRectangleGeometry.Size), sizeAnim);
            batch.End();

            batch.Completed += (_, _) =>
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (ReferenceEquals(visual.Clip, clip))
                    {
                        visual.Clip = null;
                    }

                    onCompleted?.Invoke();
                });

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void OnNativeElementSizeChangedForClip(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            UpdateFrameworkElementClip(fe);
        }
    }

    private static void UpdateFrameworkElementClip(FrameworkElement fe)
    {
        var w = fe.ActualWidth;
        var h = fe.ActualHeight;
        if (w > 0 && h > 0)
        {
            fe.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, w, h)
            };
        }
    }
}
