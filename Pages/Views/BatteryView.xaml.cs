namespace obxodka.Views;

public partial class BatteryView : ContentView
{
    private int _currentMode = 2;
    private bool _isUpdating;

    public BatteryView()
    {
        InitializeComponent();

        VersionLabel.Text = $"Версия: {AppInfo.Current.VersionString}";

        _currentMode = Preferences.Get("BatteryMode", 2);
        UpdateSelectionUI(_currentMode);

        UpdateLockState();
    }

    public void OnAppearing() => UpdateLockState();

    public async Task PlayEntranceAnimationAsync()
    {
        Opacity = 1;
        TranslationY = 0;
        await UIAnimations.PlayEntranceCascadeAsync(80, 450, EcoButton, BalancedButton, TurboButton, VersionSection);
    }



    private void UpdateLockState()
    {
        var isVpnRunning = OctopusEngine.Current != null && OctopusEngine.Current.IsConnected;

        LockWarningLabel.IsVisible = isVpnRunning;

        EcoButton.IsEnabled = !isVpnRunning;
        BalancedButton.IsEnabled = !isVpnRunning;
        TurboButton.IsEnabled = !isVpnRunning;

        var opacity = isVpnRunning ? 0.5 : 1.0;
        EcoButton.Opacity = opacity;
        BalancedButton.Opacity = opacity;
        TurboButton.Opacity = opacity;
    }

    private void OnEcoTapped(object sender, TappedEventArgs e)
    {
        if (!EcoButton.IsEnabled)
        {
            return;
        }

        SetMode(1);
    }

    private void OnBalancedTapped(object sender, TappedEventArgs e)
    {
        if (!BalancedButton.IsEnabled)
        {
            return;
        }

        SetMode(2);
    }

    private void OnTurboTapped(object sender, TappedEventArgs e)
    {
        if (!TurboButton.IsEnabled)
        {
            return;
        }

        SetMode(8);
    }

    private void SetMode(int rays)
    {
        if (_isUpdating)
        {
            return;
        }

        _isUpdating = true;

        _currentMode = rays;
        Preferences.Set("BatteryMode", rays);
        UpdateSelectionUI(rays);

        _isUpdating = false;
    }

    private void UpdateSelectionUI(int mode)
    {
        EcoButton.Stroke = Color.FromArgb("#E5E7EB");
        BalancedButton.Stroke = Color.FromArgb("#E5E7EB");
        TurboButton.Stroke = Color.FromArgb("#E5E7EB");

        EcoRadio.IsChecked = false;
        BalancedRadio.IsChecked = false;
        TurboRadio.IsChecked = false;

        var activeStroke = Color.FromArgb("#7C3AED");

        if (mode == 1)
        {
            EcoButton.Stroke = activeStroke;
            EcoRadio.IsChecked = true;
        }
        else if (mode == 2)
        {
            BalancedButton.Stroke = activeStroke;
            BalancedRadio.IsChecked = true;
        }
        else if (mode == 8)
        {
            TurboButton.Stroke = activeStroke;
            TurboRadio.IsChecked = true;
        }
    }
}
