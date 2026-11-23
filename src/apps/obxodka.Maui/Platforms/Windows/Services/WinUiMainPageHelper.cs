using Windows.Media.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
            winPlayer.MediaPlayer?.Pause();
            winPlayer.SetMediaPlayer(null);
        }
        catch
        {
            //игнорируем ошибки остановки плеера при выгрузке
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
