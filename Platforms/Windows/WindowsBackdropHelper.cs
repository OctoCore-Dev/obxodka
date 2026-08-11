using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using WinRT;

namespace obxodka.Platforms.Windows;

public static class WindowsBackdropHelper
{
    private static DesktopAcrylicController? t_acrylicController;
    private static MicaController? t_micaController;
    private static SystemBackdropConfiguration? t_configurationSource;
    private static Microsoft.UI.Xaml.Window? t_currentWindow;

    public static void ApplyBackdrop(Microsoft.UI.Xaml.Window window, string mode)
    {
        if (window == null)
        {
            return;
        }

        window.SystemBackdrop = null;

        CleanupControllers();

        if (t_currentWindow != window)
        {
            if (t_currentWindow != null)
            {
                t_currentWindow.Activated -= Window_Activated;
                t_currentWindow.Closed -= Window_Closed;
                if (t_currentWindow.Content is FrameworkElement oldFe)
                {
                    oldFe.ActualThemeChanged -= Window_ThemeChanged;
                }
            }

            t_currentWindow = window;

            t_currentWindow.Activated += Window_Activated;
            t_currentWindow.Closed += Window_Closed;
            if (t_currentWindow.Content is FrameworkElement fe)
            {
                fe.ActualThemeChanged += Window_ThemeChanged;
            }
        }

        if (mode == "None" || string.IsNullOrEmpty(mode) || mode == "Без эффекта")
        {
            return;
        }

        if (t_configurationSource == null)
        {
            t_configurationSource = new SystemBackdropConfiguration
            {
                IsInputActive = true
            };
            SetConfigurationSourceTheme();
        }

        var supportsBackdrop = t_currentWindow.As<ICompositionSupportsSystemBackdrop>();

        if (mode == "Acrylic")
        {
            if (DesktopAcrylicController.IsSupported())
            {
                t_acrylicController = new DesktopAcrylicController();

                var tintOpacity = Preferences.Get("AcrylicTintOpacity", 0.0f);
                var luminosityOpacity = Preferences.Get("AcrylicLuminosityOpacity", 0.8f);

                t_acrylicController.TintColor = Microsoft.UI.Colors.Transparent;
                t_acrylicController.TintOpacity = tintOpacity;
                t_acrylicController.LuminosityOpacity = luminosityOpacity;

                t_acrylicController.FallbackColor = global::Windows.UI.Color.FromArgb(255, 20, 20, 20);

                _ = t_acrylicController.AddSystemBackdropTarget(supportsBackdrop);
                t_acrylicController.SetSystemBackdropConfiguration(t_configurationSource);
            }
        }
        else if (mode == "Mica")
        {
            if (MicaController.IsSupported())
            {
                t_micaController = new MicaController
                {
                    Kind = MicaKind.BaseAlt,
                    FallbackColor = global::Windows.UI.Color.FromArgb(255, 20, 20, 20)
                };

                _ = t_micaController.AddSystemBackdropTarget(supportsBackdrop);
                t_micaController.SetSystemBackdropConfiguration(t_configurationSource);
            }
        }
    }

    private static void CleanupControllers()
    {
        if (t_acrylicController != null)
        {
            t_acrylicController.RemoveAllSystemBackdropTargets();
            t_acrylicController.Dispose();
            t_acrylicController = null;
        }
        if (t_micaController != null)
        {
            t_micaController.RemoveAllSystemBackdropTargets();
            t_micaController.Dispose();
            t_micaController = null;
        }
    }

    private static void Window_Activated(object sender, WindowActivatedEventArgs args) => t_configurationSource?.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated;

    private static void Window_Closed(object sender, WindowEventArgs args)
    {
        CleanupControllers();
        if (t_currentWindow != null)
        {
            t_currentWindow.Activated -= Window_Activated;
            t_currentWindow.Closed -= Window_Closed;
            if (t_currentWindow.Content is FrameworkElement fe)
            {
                fe.ActualThemeChanged -= Window_ThemeChanged;
            }

            t_currentWindow = null;
        }
        t_configurationSource = null;
    }

    private static void Window_ThemeChanged(FrameworkElement sender, object args)
    {
        if (t_configurationSource != null)
        {
            SetConfigurationSourceTheme();
        }
    }

    private static void SetConfigurationSourceTheme()
    {
        if (t_configurationSource != null)
        {
            var appTheme = Microsoft.Maui.Controls.Application.Current?.RequestedTheme;
            if (appTheme == AppTheme.Dark)
            {
                t_configurationSource.Theme = SystemBackdropTheme.Dark;
            }
            else if (appTheme == AppTheme.Light)
            {
                t_configurationSource.Theme = SystemBackdropTheme.Light;
            }
            else
            {
                if (t_currentWindow?.Content is FrameworkElement fe)
                {
                    switch (fe.ActualTheme)
                    {
                        case ElementTheme.Dark:
                            t_configurationSource.Theme = SystemBackdropTheme.Dark;
                            break;
                        case ElementTheme.Light:
                            t_configurationSource.Theme = SystemBackdropTheme.Light;
                            break;
                        case ElementTheme.Default:
                            break;
                        default:
                            t_configurationSource.Theme = SystemBackdropTheme.Default;
                            break;
                    }
                }
                else
                {
                    t_configurationSource.Theme = SystemBackdropTheme.Default;
                }
            }
        }
    }
}
