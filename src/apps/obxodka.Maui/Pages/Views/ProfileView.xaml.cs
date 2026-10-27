namespace obxodka.Views;

public sealed partial class ProfileView : ContentView
{
    private static Color ErrorColor =>
        Application.Current?.Resources.TryGetValue("Error", out var val) == true && val is Color c ? c : Color.FromArgb("#EF4444");

    private static Color AccentColor =>
        Application.Current?.Resources.TryGetValue("Accent", out var val) == true && val is Color c ? c : Color.FromArgb("#00E5FF");

    private long _lastRemainingSeconds;
    private MainPage _parent = null!;
    private ApiService? _apiService;
    private string? _myReferralCode;
    public event EventHandler? BuyTokensRequested;
    public event EventHandler? LogoutRequested;
    public event EventHandler? BalanceRefreshRequested;

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

        var isAdblock = Preferences.Default.Get("use_adblock_dns", true);
        AdBlockSwitch.IsToggled = isAdblock;
        DnsAdBlocker.IsAdBlockEnabled = isAdblock;
        TelemetrySwitch.IsToggled = Preferences.Default.Get("use_telemetry", true);

        if (DeviceInfo.Idiom != DeviceIdiom.Phone)
        {
            CardLogout.IsVisible = false;
        }

        ProfileScrollView.SizeChanged += (s, e) =>
        {
            if (ProfileScrollView.Width > 0)
            {
                ApplyCardWidth(ProfileScrollView.Width);
            }
        };

        ProfileScrollView.Scrolled += (s, e) =>
            CardStackScrollHelper.ApplyToChildren(
                ProfileScrollView,
                ContentGrid.Children,
                zone: 65.0,
                minScale: 0.92,
                power: 2.5);

        Unloaded += OnUnloaded;
    }

    public void ForceLayoutWidth()
    {
        if (ProfileScrollView.Width > 0)
        {
            ApplyCardWidth(ProfileScrollView.Width);
        }
        else if (Width > 0 && RootLayoutGrid is not null)
        {
            var avail = Width - RootLayoutGrid.Padding.HorizontalThickness;
            if (avail > 0)
            {
                ApplyCardWidth(avail);
            }
        }
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width > 0 && RootLayoutGrid is not null)
        {
            var availableWidth = width - RootLayoutGrid.Padding.HorizontalThickness;
            if (availableWidth > 0)
            {
                ApplyCardWidth(availableWidth);
            }
        }
    }

    private void ApplyCardWidth(double targetWidth)
    {
        if (targetWidth <= 0 || ContentGrid is null)
        {
            return;
        }

        var safeWidth = Math.Min(targetWidth - 6, 950);
        if (safeWidth <= 0)
        {
            return;
        }

        ContentGrid.WidthRequest = safeWidth;
        ContentGrid.MaximumWidthRequest = safeWidth;
    }

    public void Initialize(MainPage parent, ThemeManager? _ = null, ApiService? apiService = null)
    {
        _parent = parent;
        _apiService = apiService;
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
        await this.PlayCardsEntranceAsync(35, 240);
    }

    public async Task LoadReferralCodeAsync()
    {
        if (!CardPromo.IsVisible || _apiService is null)
        {
            return;
        }

        try
        {
            var (success, data, _) = await _apiService.GetMyReferralCodeAsync();
            if (success && data is not null && !string.IsNullOrWhiteSpace(data.Code))
            {
                _myReferralCode = data.Code;
                MainThread.BeginInvokeOnMainThread(() => MyReferralCodeLabel.Text = data.Code);
            }
        }
        catch { }
    }

    private async void OnCopyReferralCodeClickedAsync(object? sender, TappedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_myReferralCode))
        {
            await LoadReferralCodeAsync();
        }

        if (!string.IsNullOrWhiteSpace(_myReferralCode))
        {
            await Clipboard.Default.SetTextAsync(_myReferralCode);
            await _parent.DisplayAlertAsync("Скопировано", $"Ваш реферальный код {_myReferralCode} скопирован в буфер обмена.", "OK");
        }
    }

    private async void OnActivatePromoClickedAsync(object? sender, EventArgs e)
    {
        if (_apiService is null)
        {
            return;
        }

        var code = PromoCodeEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            await _parent.DisplayAlertAsync("Внимание", "Введите промокод или реферальный код друга.", "OK");
            return;
        }

        await UIAnimations.SetButtonLoadingAsync(BtnActivatePromoBorder, BtnActivatePromo, LoaderActivatePromo, true);
        try
        {
            var (success, data, error) = await _apiService.ActivateReferralCodeAsync(code);
            if (success)
            {
                PromoCodeEntry.Text = string.Empty;
                BalanceRefreshRequested?.Invoke(this, EventArgs.Empty);
                await _parent.DisplayAlertAsync("Успех", data?.Message ?? "Код успешно активирован!", "OK");
            }
            else
            {
                await _parent.DisplayAlertAsync("Ошибка", ApiErrorHandler.ParseGeneralError(error, "Не удалось активировать код."), "OK");
            }
        }
        finally
        {
            await UIAnimations.SetButtonLoadingAsync(BtnActivatePromoBorder, BtnActivatePromo, LoaderActivatePromo, false);
        }
    }

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

        if (session.SubscriptionUntil.HasValue && session.SubscriptionUntil.Value > DateTime.UtcNow)
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
        _lastRemainingSeconds = remainingSeconds;
        if (remainingSeconds <= 0)
        {
            ProfileTokenLabel.Text = "0ч 00м 00с";
            ProfileTokenLabel.TextColor = ErrorColor;
        }
        else
        {
            ProfileTokenLabel.Text = TimeFormatHelper.FormatSeconds(remainingSeconds, true);
            ProfileTokenLabel.TextColor = AccentColor;
        }
    }

    public void UpdateCardOpacity()
    {
        var bgSurface = (Application.Current?.Resources.TryGetValue("BgSurface", out var bg) == true && bg is Color bgColor)
            ? bgColor
            : Color.FromArgb("#161622");
        var borderSubtle = (Application.Current?.Resources.TryGetValue("BorderSubtle", out var bs) == true && bs is Color bsc)
            ? bsc
            : Color.FromArgb("#282838");

        CardProfile.BackgroundColor = bgSurface;
        CardProfile.Stroke = borderSubtle;
        CardAdBlock.BackgroundColor = bgSurface;
        CardAdBlock.Stroke = borderSubtle;
        CardBalance.BackgroundColor = bgSurface;
        CardBalance.Stroke = borderSubtle;
        CardTelemetry.BackgroundColor = bgSurface;
        CardTelemetry.Stroke = borderSubtle;
        CardPromo.BackgroundColor = bgSurface;
        CardPromo.Stroke = borderSubtle;
        CardLogout.BackgroundColor = bgSurface;
        CardLogout.Stroke = borderSubtle;
        CardDeleteAccount.BackgroundColor = bgSurface;
        CardDeleteAccount.Stroke = borderSubtle;
    }

    public void SetHeaderTopInset(double top)
    {
        if (DeviceInfo.Platform == DevicePlatform.Android && DeviceInfo.Idiom == DeviceIdiom.Phone)
        {
            RootLayoutGrid?.Padding = new Thickness(16, 0, 16, 0);

            if (ContentGrid != null)
            {
                var topAir = Math.Max(top + 16, 50);
                ContentGrid.Padding = new Thickness(0, topAir, 0, 140);
            }
        }
    }

    public void OnThemeChanged() =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateCardOpacity();
            UpdateBalance(_lastRemainingSeconds);
        });

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
        DnsAdBlocker.IsAdBlockEnabled = e.Value;
    }

    private void OnTelemetryToggled(object? sender, ToggledEventArgs e) =>
        Preferences.Default.Set("use_telemetry", e.Value);
}
