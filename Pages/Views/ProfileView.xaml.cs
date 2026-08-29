namespace obxodka.Views;

public sealed partial class ProfileView : ContentView
{
    private static readonly Color t_errorColor = Color.FromArgb("#EF4444");
    private static readonly Color t_cyanColor = Color.FromArgb("#00E5FF");

    private MainPage _parent = null!;
    public event EventHandler? BuyTokensRequested;
    public event EventHandler? LogoutRequested;
    public event EventHandler? FriendsRequested;

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

        if (DeviceInfo.Idiom == DeviceIdiom.Phone)
        {
            var content = Content;
            Content = new ScrollView
            {
                Orientation = ScrollOrientation.Vertical,
                VerticalScrollBarVisibility = ScrollBarVisibility.Never,
                Content = content
            };
        }

        AdBlockSwitch.IsToggled = Preferences.Default.Get("use_adblock_dns", false);
        TelemetrySwitch.IsToggled = Preferences.Default.Get("use_telemetry", true);
        MeshSwitch.IsToggled = MeshSettings.MeshEnabled;

        if (DeviceInfo.Idiom != DeviceIdiom.Phone)
        {
            CardMesh.IsVisible = false;
            CardFriends.IsVisible = false;
            CardLogout.IsVisible = false;
        }

        Unloaded += OnUnloaded;
    }

    public void Initialize(MainPage parent)
    {
        _parent = parent;
        _parent.VpnService.OnStateChanged += OnVpnStateChanged;
        IsEditingAllowed = _parent.VpnService.CurrentState == AppVpnState.Disconnected;
    }

    private void OnUnloaded(object? sender, EventArgs e) => _parent?.VpnService.OnStateChanged -= OnVpnStateChanged;

    private void OnVpnStateChanged(AppVpnState s) =>
        MainThread.BeginInvokeOnMainThread(() => IsEditingAllowed = s == AppVpnState.Disconnected);

    public async Task PlayEntranceAnimationAsync()
    {
        Opacity = 1;
        TranslationY = 0;

        var visibleCards = new[] { CardProfile, CardBalance, CardAdBlock, CardTelemetry, CardMesh, CardFriends, CardLogout, CardDeleteAccount }
            .Where(c => c.IsVisible)
            .ToArray();

        if (visibleCards.Length > 0)
        {
            await UIAnimations.PlayEntranceCascadeAsync(80, 450, visibleCards);
        }
    }

    private void OnMeshToggled(object? sender, ToggledEventArgs e) => MeshSettings.MeshEnabled = e.Value;

    private void OnFriendsTapped(object? sender, EventArgs e) => FriendsRequested?.Invoke(this, EventArgs.Empty);

    public void UpdateProfileInfo(UserSession session)
    {
        ProfileEmailLabel.Text = session.Email ?? "Unknown Email";

        var providerName = "EMAIL";
        var iconSource = "email_logo.png";

        if (session.Email is { Length: > 0 } email && email.IndexOf('@') is var atIdx && atIdx >= 0)
        {
            var domain = email.AsSpan(atIdx + 1);

            if (domain.Equals("gmail.com", StringComparison.OrdinalIgnoreCase))
            {
                providerName = "GOOGLE";
                iconSource = "google_logo.png";
            }
            else if (domain.StartsWith("yandex.", StringComparison.OrdinalIgnoreCase) || domain.Equals("ya.ru", StringComparison.OrdinalIgnoreCase))
            {
                providerName = "YANDEX";
                iconSource = "yandex_logo.png";
            }
            else if (domain.Equals("mail.ru", StringComparison.OrdinalIgnoreCase) ||
                     domain.Equals("inbox.ru", StringComparison.OrdinalIgnoreCase) ||
                     domain.Equals("list.ru", StringComparison.OrdinalIgnoreCase) ||
                     domain.Equals("bk.ru", StringComparison.OrdinalIgnoreCase) ||
                     domain.Equals("internet.ru", StringComparison.OrdinalIgnoreCase))
            {
                providerName = "MAIL.RU";
                iconSource = "mailru_logo.png";
            }
            else if (domain.Equals("outlook.com", StringComparison.OrdinalIgnoreCase) ||
                     domain.Equals("hotmail.com", StringComparison.OrdinalIgnoreCase) ||
                     domain.Equals("live.com", StringComparison.OrdinalIgnoreCase))
            {
                providerName = "MICROSOFT";
                iconSource = "microsoft_logo.png";
            }
            else if (domain.Equals("icloud.com", StringComparison.OrdinalIgnoreCase) ||
                     domain.Equals("me.com", StringComparison.OrdinalIgnoreCase) ||
                     domain.Equals("mac.com", StringComparison.OrdinalIgnoreCase))
            {
                providerName = "APPLE";
                iconSource = "apple_logo.png";
            }
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
            ProfileTokenLabel.Text = "0ч 00м 00с";
            ProfileTokenLabel.TextColor = t_errorColor;
        }
        else
        {
            ProfileTokenLabel.Text = TimeFormatHelper.FormatSeconds(remainingSeconds, true);
            ProfileTokenLabel.TextColor = t_cyanColor;
        }
    }

    private void OnBuyTokensClicked(object? sender, EventArgs e) =>
        BuyTokensRequested?.Invoke(this, EventArgs.Empty);

    private void OnDeleteAccountClicked(object? sender, EventArgs e) =>
        _ = _parent.SwitchTabAsync("delete");

    private void OnLogoutClicked(object? sender, EventArgs e) =>
        LogoutRequested?.Invoke(this, EventArgs.Empty);

    private void OnAdBlockToggled(object? sender, ToggledEventArgs e)
    {
        if (!IsEditingAllowed)
        {
            AdBlockSwitch.IsToggled = !e.Value;
            return;
        }

        Preferences.Default.Set("use_adblock_dns", e.Value);
    }

    private void OnTelemetryToggled(object? sender, ToggledEventArgs e) =>
        Preferences.Default.Set("use_telemetry", e.Value);

    private async void OnPointerEnteredAsync(object? sender, PointerEventArgs e)
    {
        if (sender is VisualElement ve && ve.IsEnabled)
        {
            _ = await ve.ScaleToAsync(1.02, 120, Easing.CubicOut);
        }
    }

    private async void OnPointerExitedAsync(object? sender, PointerEventArgs e)
    {
        if (sender is VisualElement ve && ve.IsEnabled)
        {
            _ = await ve.ScaleToAsync(1.0, 120, Easing.CubicIn);
        }
    }
}
