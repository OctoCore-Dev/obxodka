namespace obxodka.Views;

public partial class AppearanceView : ContentView
{
    private string _backdropMode = "Acrylic";
    private string _themeMode = "System";
    private string _accentColor = "#0078D4";

    public AppearanceView()
    {
        InitializeComponent();

        _backdropMode = Preferences.Get("WindowsBackdropMode", "Acrylic");
        _themeMode = Preferences.Get("AppThemeMode", "System");
        _accentColor = Preferences.Get("AppAccentColor", "#0078D4");

        var tintOpacity = Preferences.Get("AcrylicTintOpacity", 0.0f);
        var luminosityOpacity = Preferences.Get("AcrylicLuminosityOpacity", 0.8f);

        TintSlider.Value = tintOpacity;
        LuminositySlider.Value = luminosityOpacity;

        UpdateTintLabel(tintOpacity);
        UpdateLuminosityLabel(luminosityOpacity);

        UpdateSelectionUI(_backdropMode, _themeMode, _accentColor);
    }

    public async Task PlayEntranceAnimationAsync()
    {
        Opacity = 1;
        TranslationY = 0;
        await UIAnimations.PlayEntranceCascadeAsync(80, 450,
            HeaderSection,
            DescriptionSection,
            WindowsBackdropSection,
            AcrylicSettingsSection,
            ColorsSection,
            ThemeSection);
    }

    private void OnAcrylicTapped(object sender, TappedEventArgs e) => SetBackdrop("Acrylic");
    private void OnMicaTapped(object sender, TappedEventArgs e) => SetBackdrop("Mica");
    private void OnOffBackdropTapped(object sender, TappedEventArgs e) => SetBackdrop("Off");

    private void OnThemeTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is string theme)
        {
            SetTheme(theme);
        }
    }

    private void OnColorTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is string colorHex)
        {
            SetAccentColor(colorHex);
        }
    }

    private void OnTintSliderValueChanged(object sender, ValueChangedEventArgs e)
    {
        var value = (float)e.NewValue;
        Preferences.Set("AcrylicTintOpacity", value);
        UpdateTintLabel(value);
        UpdateWindowsBackdrop();
    }

    private void OnLuminositySliderValueChanged(object sender, ValueChangedEventArgs e)
    {
        var value = (float)e.NewValue;
        Preferences.Set("AcrylicLuminosityOpacity", value);
        UpdateLuminosityLabel(value);
        UpdateWindowsBackdrop();
    }

    private void UpdateTintLabel(float value) => TintValueLabel.Text = $"{(int)(value * 100)}%";

    private void UpdateLuminosityLabel(float value) => LuminosityValueLabel.Text = $"{(int)(value * 100)}%";

    private void SetBackdrop(string mode)
    {
        try
        {
            _backdropMode = mode;
            Preferences.Set("WindowsBackdropMode", mode);
            UpdateSelectionUI(_backdropMode, _themeMode, _accentColor);
            UpdateWindowsBackdrop();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in SetBackdrop: {ex}");
        }
    }

    private void SetTheme(string theme)
    {
        try
        {
            _themeMode = theme;
            Preferences.Set("AppThemeMode", theme);

            UpdateSelectionUI(_backdropMode, _themeMode, _accentColor);

            Application.Current?.UserAppTheme = theme == "Dark" ? AppTheme.Dark : theme == "Light" ? AppTheme.Light : AppTheme.Unspecified;

        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in SetTheme: {ex}");
        }
    }

    private void SetAccentColor(string hex)
    {
        try
        {
            _accentColor = hex;
            Preferences.Set("AppAccentColor", hex);

            UpdateSelectionUI(_backdropMode, _themeMode, _accentColor);

            var newColor = Color.FromArgb(hex);
            if (Application.Current != null)
            {
                Application.Current.Resources["Primary"] = newColor;
                Application.Current.Resources["PrimaryBright"] = newColor.WithLuminosity(Math.Min(newColor.GetLuminosity() + 0.1f, 1.0f));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in SetAccentColor: {ex}");
        }
    }

#pragma warning disable CA1822
    private void UpdateWindowsBackdrop()
    {
#if WINDOWS
        var firstWindow = Application.Current?.Windows.Count > 0 ? Application.Current.Windows[0] : null;
        if (firstWindow?.Handler?.PlatformView is Microsoft.UI.Xaml.Window winUIWindow)
        {
            Platforms.Windows.WindowsBackdropHelper.ApplyBackdrop(winUIWindow, _backdropMode);
        }
#endif
    }
#pragma warning restore CA1822

    private void UpdateSelectionUI(string backdropMode, string themeMode, string accentColor)
    {
        var inactiveStroke = Color.FromArgb("#1AFFFFFF");
        var activeStroke = Color.FromArgb("#0078D4");

        AcrylicButton.Stroke = inactiveStroke;
        MicaButton.Stroke = inactiveStroke;
        OffBackdropButton.Stroke = inactiveStroke;

        if (backdropMode == "Acrylic")
        {
            AcrylicButton.Stroke = activeStroke;
        }
        else if (backdropMode == "Mica")
        {
            MicaButton.Stroke = activeStroke;
        }
        else
        {
            OffBackdropButton.Stroke = activeStroke;
        }

        ThemeSystemButton.Stroke = inactiveStroke;
        ThemeDarkButton.Stroke = inactiveStroke;
        ThemeLightButton.Stroke = inactiveStroke;

        if (themeMode == "System")
        {
            ThemeSystemButton.Stroke = activeStroke;
        }
        else if (themeMode == "Dark")
        {
            ThemeDarkButton.Stroke = activeStroke;
        }
        else if (themeMode == "Light")
        {
            ThemeLightButton.Stroke = activeStroke;
        }

        ColorBlue.Stroke = inactiveStroke;
        ColorPurple.Stroke = inactiveStroke;
        ColorCyan.Stroke = inactiveStroke;
        ColorRed.Stroke = inactiveStroke;
        ColorGreen.Stroke = inactiveStroke;

        ColorBlue.StrokeThickness = 0;
        ColorPurple.StrokeThickness = 0;
        ColorCyan.StrokeThickness = 0;
        ColorRed.StrokeThickness = 0;
        ColorGreen.StrokeThickness = 0;

        if (accentColor == "#0078D4")
        { ColorBlue.Stroke = activeStroke; ColorBlue.StrokeThickness = 3; }
        else if (accentColor == "#8B5CF6")
        { ColorPurple.Stroke = activeStroke; ColorPurple.StrokeThickness = 3; }
        else if (accentColor == "#00E5FF")
        { ColorCyan.Stroke = activeStroke; ColorCyan.StrokeThickness = 3; }
        else if (accentColor == "#EF4444")
        { ColorRed.Stroke = activeStroke; ColorRed.StrokeThickness = 3; }
        else if (accentColor == "#10B981")
        { ColorGreen.Stroke = activeStroke; ColorGreen.StrokeThickness = 3; }
    }

    private async void OnPointerEnteredAsync(object sender, PointerEventArgs e)
    {
        if (sender is View view)
        {
            _ = await view.ScaleToAsync(1.05, 150, Easing.CubicOut);
        }
    }

    private async void OnPointerExitedAsync(object sender, PointerEventArgs e)
    {
        if (sender is View view)
        {
            _ = await view.ScaleToAsync(1.0, 150, Easing.CubicIn);
        }
    }
}
