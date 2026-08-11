namespace obxodka.Views;

public partial class ConfigurationView : ContentView
{
    private int _currentMode = 2;
    private bool _useHttp3;
    private bool _isUpdating;

    public ConfigurationView()
    {
        InitializeComponent();

        VersionLabel.Text = $"Версия: {AppInfo.Current.VersionString}";

        _currentMode = Preferences.Get("BatteryMode", 2);
        _useHttp3 = Preferences.Get("UseHttp3", false);

        UpdateSelectionUI(_currentMode, _useHttp3);

        UpdateLockState();
    }

    public void OnAppearing() => UpdateLockState();

    public async Task PlayEntranceAnimationAsync()
    {
        Opacity = 1;
        TranslationY = 0;
        await UIAnimations.PlayEntranceCascadeAsync(80, 450, EcoButton, BalancedButton, TurboButton, Http2Button, Http3Button, VersionSection);
    }

    private void UpdateLockState()
    {
        var isVpnRunning = OctopusEngine.Current != null && OctopusEngine.Current.IsConnected;

        LockWarningLabel.IsVisible = isVpnRunning;

        EcoButton.IsEnabled = !isVpnRunning;
        BalancedButton.IsEnabled = !isVpnRunning;
        TurboButton.IsEnabled = !isVpnRunning;
        Http2Button.IsEnabled = !isVpnRunning;
        Http3Button.IsEnabled = !isVpnRunning;

        var opacity = isVpnRunning ? 0.5 : 1.0;
        EcoButton.Opacity = opacity;
        BalancedButton.Opacity = opacity;
        TurboButton.Opacity = opacity;
        Http2Button.Opacity = opacity;
        Http3Button.Opacity = opacity;
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

    private void OnHttp2Tapped(object sender, TappedEventArgs e)
    {
        if (!Http2Button.IsEnabled)
        {
            return;
        }

        SetProtocol(false);
    }

    private void OnHttp3Tapped(object sender, TappedEventArgs e)
    {
        if (!Http3Button.IsEnabled)
        {
            return;
        }

        SetProtocol(true);
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
        UpdateSelectionUI(_currentMode, _useHttp3);

        _isUpdating = false;
    }

    private void SetProtocol(bool useHttp3)
    {
        if (_isUpdating)
        {
            return;
        }

        _isUpdating = true;

        _useHttp3 = useHttp3;
        Preferences.Set("UseHttp3", useHttp3);
        UpdateSelectionUI(_currentMode, _useHttp3);

        _isUpdating = false;
    }

    private void UpdateSelectionUI(int mode, bool useHttp3)
    {
        var inactiveStroke = Color.FromArgb("#1AFFFFFF");
        var activeStroke = Color.FromArgb("#0078D4");

        EcoButton.Stroke = inactiveStroke;
        BalancedButton.Stroke = inactiveStroke;
        TurboButton.Stroke = inactiveStroke;
        Http2Button.Stroke = inactiveStroke;
        Http3Button.Stroke = inactiveStroke;

        var raysText = "";
        if (mode == 1)
        {
            EcoButton.Stroke = activeStroke;
            raysText = "1 Луч";
        }
        else if (mode == 2)
        {
            BalancedButton.Stroke = activeStroke;
            raysText = "2 Луча";
        }
        else if (mode == 8)
        {
            TurboButton.Stroke = activeStroke;
            raysText = "8 Лучей";
        }

        string protocolText;
        if (useHttp3)
        {
            Http3Button.Stroke = activeStroke;
            protocolText = "HTTP/3 QUIC";
        }
        else
        {
            Http2Button.Stroke = activeStroke;
            protocolText = "HTTP/2";
        }

        CurrentSelectionLabel.Text = $"[ {raysText} / {protocolText} ]";
    }
}

