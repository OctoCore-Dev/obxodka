namespace obxodka.Views;

public partial class ProfileView : ContentView
{
    private MainPage _parent = null!;
    public event EventHandler? BuyTokensRequested;
    public event EventHandler? LogoutRequested;

    public static readonly BindableProperty IsEditingAllowedProperty =
        BindableProperty.Create(nameof(IsEditingAllowed), typeof(bool), typeof(ProfileView), true);

    public bool IsEditingAllowed
    {
        get => (bool)GetValue(IsEditingAllowedProperty);
        set => SetValue(IsEditingAllowedProperty, value);
    }

    public ProfileView()
    {
        InitializeComponent();
        AdBlockSwitch.IsToggled = Preferences.Default.Get("use_adblock_dns", false);
        TelemetrySwitch.IsToggled = Preferences.Default.Get("use_telemetry", true);
    }

    public void Initialize(MainPage parent)
    {
        _parent = parent;
        _parent.VpnService.OnStateChanged += (s) => MainThread.BeginInvokeOnMainThread(() => IsEditingAllowed = s == AppVpnState.Disconnected);
        IsEditingAllowed = _parent.VpnService.CurrentState == AppVpnState.Disconnected;
    }

    public async Task PlayEntranceAnimationAsync()
    {
        Opacity = 1;
        TranslationY = 0;
        await UIAnimations.PlayEntranceCascadeAsync(80, 450, CardProfile, CardBalance, CardAdBlock, CardTelemetry, CardLogout, CardDeleteAccount);
    }

    public void UpdateProfileInfo(UserSession session)
    {
        ProfileEmailLabel.Text = session.Email ?? "Unknown Email";

        var domain = session.Email?.Split('@').LastOrDefault()?.ToLowerInvariant();
        var providerName = "EMAIL";
        var iconSource = "email_logo.png";

        switch (domain)
        {
            case "gmail.com":
                providerName = "GOOGLE";
                iconSource = "google_logo.png";
                break;
            case "yandex.ru":
            case "yandex.com":
            case "yandex.kz":
            case "yandex.by":
            case "ya.ru":
                providerName = "YANDEX";
                iconSource = "yandex_logo.png";
                break;
            case "mail.ru":
            case "inbox.ru":
            case "list.ru":
            case "bk.ru":
            case "internet.ru":
                providerName = "MAIL.RU";
                iconSource = "mailru_logo.png";
                break;
            case "outlook.com":
            case "hotmail.com":
            case "live.com":
                providerName = "MICROSOFT";
                iconSource = "microsoft_logo.png";
                break;
            case "icloud.com":
            case "me.com":
            case "mac.com":
                providerName = "APPLE";
                iconSource = "apple_logo.png";
                break;
            default:
                break;
        }

        ProfileProviderLabel.Text = $"{providerName} АККАУНТ";
        ProfileProviderIcon.Source = iconSource;

        if (session.SubscriptionUntil.HasValue)
        {
            SubscriptionContainer.IsVisible = true;
            var dt = session.SubscriptionUntil.Value.ToLocalTime();
            ProfileSubLabel.Text = $"до {dt:dd.MM.yyyy HH:mm}";
        }
        else
        {
            SubscriptionContainer.IsVisible = false;
        }
    }

    public void UpdateBalance(long remainingSeconds)
    {
        if (remainingSeconds <= 0)
        {
            ProfileTokenLabel.Text = "0ч 00м";
            ProfileTokenLabel.TextColor = Application.Current?.Resources.TryGetValue("Error", out var errorColor) == true
                ? (Color)errorColor
                : Color.FromArgb("#EF4444");
        }
        else
        {
            var ts = TimeSpan.FromSeconds(remainingSeconds);
            ProfileTokenLabel.Text = $"{(int)ts.TotalHours}ч {ts.Minutes:D2}м";
            ProfileTokenLabel.TextColor = Application.Current?.Resources.TryGetValue("Accent", out var cyanColor) == true
                ? (Color)cyanColor
                : Color.FromArgb("#00E5FF");
        }
    }

    private void OnBuyTokensClickedAsync(object? sender, EventArgs e) => BuyTokensRequested?.Invoke(this, EventArgs.Empty);

    private void OnDeleteAccountClicked(object? sender, EventArgs e) => _ = _parent.SwitchTabAsync("delete");

    private void OnLogoutClicked(object? sender, EventArgs e) => LogoutRequested?.Invoke(this, EventArgs.Empty);

    private void OnAdBlockToggled(object sender, ToggledEventArgs e)
    {
        if (!IsEditingAllowed)
        {
            AdBlockSwitch.IsToggled = !e.Value;
            return;
        }
        Preferences.Default.Set("use_adblock_dns", e.Value);
    }

    private void OnTelemetryToggled(object sender, ToggledEventArgs e) => Preferences.Default.Set("use_telemetry", e.Value);
}
