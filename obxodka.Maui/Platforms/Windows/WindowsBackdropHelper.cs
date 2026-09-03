using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT;
using WinRT.Interop;
using SolidColorBrush = Microsoft.UI.Xaml.Media.SolidColorBrush;

namespace obxodka.Platforms.Windows;

[SupportedOSPlatform("windows")]
public static partial class WindowsBackdropHelper
{
    private static readonly SolidColorBrush t_transparentBrush = new(Microsoft.UI.Colors.Transparent);

    private static DesktopAcrylicController? t_acrylicController;
    private static SystemBackdropConfiguration? t_configurationSource;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public static void ApplyBackdrop(Microsoft.UI.Xaml.Window? window)
    {
        if (window is null)
        {
            return;
        }

        try
        {
            window.SystemBackdrop = null;

            var isLight = Microsoft.Maui.Controls.Application.Current?.RequestedTheme == AppTheme.Light;
            var hwnd = WindowNative.GetWindowHandle(window);

            if (hwnd != IntPtr.Zero)
            {
                var darkMode = isLight ? 0 : 1;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
            }

            if (window.Content is FrameworkElement root)
            {
                root.RequestedTheme = isLight ? ElementTheme.Light : ElementTheme.Dark;

                CleanTree(root);

                root.Loaded += (_, _) =>
                {
                    CleanTree(root);

                    if (root.DispatcherQueue is { } dq)
                    {
                        _ = dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                            () => CleanTree(root));
                    }
                };

                root.ActualThemeChanged += (sender, _) =>
                {
                    if (t_configurationSource is { } config)
                    {
                        config.Theme = sender.ActualTheme switch
                        {
                            ElementTheme.Dark => SystemBackdropTheme.Dark,
                            ElementTheme.Light => SystemBackdropTheme.Light,
                            ElementTheme.Default => throw new NotImplementedException(),
                            _ => SystemBackdropTheme.Default
                        };
                    }
                };
            }

            if (DesktopAcrylicController.IsSupported())
            {
                t_acrylicController?.Dispose();
                t_configurationSource = new SystemBackdropConfiguration
                {
                    IsInputActive = true,
                    Theme = isLight ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark
                };

                t_acrylicController = new DesktopAcrylicController
                {
                    Kind = DesktopAcrylicKind.Base
                };

                window.Activated += (_, e) =>
                {
                    if (t_configurationSource is { } config)
                    {
                        config.IsInputActive = e.WindowActivationState != WindowActivationState.Deactivated;
                    }
                };

                _ = t_acrylicController.AddSystemBackdropTarget(window.As<ICompositionSupportsSystemBackdrop>());
                t_acrylicController.SetSystemBackdropConfiguration(t_configurationSource);
            }
            else
            {
                window.SystemBackdrop = new DesktopAcrylicBackdrop();
            }
        }
        catch
        {
            try
            {
                window.SystemBackdrop = new DesktopAcrylicBackdrop();
            }
            catch { }
        }
    }

    private static void CleanTree(DependencyObject element)
    {
        if (element is FrameworkElement fe)
        {
            if (fe.Name is "AppTitleBar" or "TitleBar" or "CustomTitleBar" or "HeaderArea" or "HeaderContent" or "TitleBarArea" or "NavHeader" or "NavigationBarArea" or "Header")
            {
                fe.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                fe.Height = 0;
                fe.MinHeight = 0;
                fe.MaxHeight = 0;
            }

            if (fe is NavigationView nv)
            {
                nv.Header = null;
                nv.AlwaysShowHeader = false;
                nv.IsPaneVisible = false;
                nv.IsPaneToggleButtonVisible = false;
                nv.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
                nv.PaneDisplayMode = NavigationViewPaneDisplayMode.LeftMinimal;
            }

            if (fe is Microsoft.UI.Xaml.Controls.Grid g && g.RowDefinitions.Count > 1)
            {
                if (g.RowDefinitions[0].Height.GridUnitType == Microsoft.UI.Xaml.GridUnitType.Pixel &&
                    g.RowDefinitions[0].Height.Value is > 0 and <= 64)
                {
                    g.RowDefinitions[0].Height = new Microsoft.UI.Xaml.GridLength(0);
                }
            }
        }

        if (element is Panel p && p.Background != t_transparentBrush)
        {
            p.Background = t_transparentBrush;
        }
        else if (element is Control c && c.Background != t_transparentBrush)
        {
            c.Background = t_transparentBrush;
        }
        else if (element is Microsoft.UI.Xaml.Controls.Border b && b.Background != t_transparentBrush)
        {
            b.Background = t_transparentBrush;
        }
        else if (element is Microsoft.UI.Xaml.Controls.ContentPresenter cp && cp.Background != t_transparentBrush)
        {
            cp.Background = t_transparentBrush;
        }
        else if (element is ContentControl cc && cc.Background != t_transparentBrush)
        {
            cc.Background = t_transparentBrush;
        }
        else if (element is UserControl uc && uc.Background != t_transparentBrush)
        {
            uc.Background = t_transparentBrush;
        }

        var count = VisualTreeHelper.GetChildrenCount(element);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            CleanTree(child);
        }
    }
}

