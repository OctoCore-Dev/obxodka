using Windows.Media.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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

    public static Action AttachNativeScrollViewer(VisualElement element, Action<double> onVerticalOffsetChanged)
    {
        ScrollViewer? attachedScrollViewer = null;
        FrameworkElement? attachedHost = null;
        var isDetached = false;

        void OnScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            if (isDetached || sender is not ScrollViewer sv)
            {
                return;
            }

            //переводим логические единицы прокрутки winui listview в dip, если viewport выражен в элементах
            var offsetDip = sv.ViewportHeight > 0.1 && sv.ActualHeight > 0.1 && sv.ViewportHeight < sv.ActualHeight * 0.5
                ? sv.VerticalOffset * (sv.ActualHeight / sv.ViewportHeight)
                : sv.VerticalOffset;
            onVerticalOffsetChanged(offsetDip);
        }

        void TryAttachToScrollViewer()
        {
            if (isDetached || attachedScrollViewer is not null || element.Handler?.PlatformView is not DependencyObject root)
            {
                return;
            }

            var sv = FindDescendantScrollViewer(root);
            if (sv is null)
            {
                return;
            }

            attachedScrollViewer = sv;
            sv.ViewChanged += OnScrollViewerViewChanged;
            if (attachedHost is not null)
            {
                attachedHost.Loaded -= OnHostLoadedOrUpdated;
                attachedHost.LayoutUpdated -= OnHostLayoutUpdated;
            }
        }

        void OnHostLoadedOrUpdated(object sender, RoutedEventArgs e) => TryAttachToScrollViewer();
        void OnHostLayoutUpdated(object? sender, object e) => TryAttachToScrollViewer();

        void BindHost()
        {
            if (isDetached)
            {
                return;
            }

            if (attachedHost is not null)
            {
                attachedHost.Loaded -= OnHostLoadedOrUpdated;
                attachedHost.LayoutUpdated -= OnHostLayoutUpdated;
                attachedHost = null;
            }

            if (element.Handler?.PlatformView is FrameworkElement fe)
            {
                attachedHost = fe;
                TryAttachToScrollViewer();
                if (attachedScrollViewer is null)
                {
                    fe.Loaded += OnHostLoadedOrUpdated;
                    fe.LayoutUpdated += OnHostLayoutUpdated;
                }
            }
        }

        void OnHandlerChanged(object? sender, EventArgs e) => BindHost();

        element.HandlerChanged += OnHandlerChanged;
        BindHost();

        return () =>
        {
            isDetached = true;
            element.HandlerChanged -= OnHandlerChanged;
            if (attachedHost is not null)
            {
                attachedHost.Loaded -= OnHostLoadedOrUpdated;
                attachedHost.LayoutUpdated -= OnHostLayoutUpdated;
                attachedHost = null;
            }

            attachedScrollViewer?.ViewChanged -= OnScrollViewerViewChanged;
            attachedScrollViewer = null;
        };
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer direct)
        {
            return direct;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv)
            {
                return sv;
            }

            var nested = FindDescendantScrollViewer(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
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
