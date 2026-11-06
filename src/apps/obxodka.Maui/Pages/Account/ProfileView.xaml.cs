namespace obxodka.Pages;

//экран профиля пользователя, баланса, adblock и рефералов
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

        var isAdblock = Preferences.Default.Get(AppPreferenceKeys.UseAdblockDns, true);
        AdBlockSwitch.IsToggled = isAdblock;
        DnsAdBlocker.IsAdBlockEnabled = isAdblock;
        UpdateAdBlockSubtitle();
        TelemetrySwitch.IsToggled = Preferences.Default.Get(AppPreferenceKeys.UseTelemetry, true);

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

    //расчет ширины для перестроения интерфейса
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

    //подгонка ширины карточек с ограничением максимума
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

    //инициализация зависимостей и подписка на статус vpn
    public void Initialize(MainPage parent, ThemeManager? _ = null, ApiService? apiService = null)
    {
        if (_parent?.VpnService is not null)
        {
            _parent.VpnService.OnStateChanged -= OnVpnStateChanged;
        }

        _parent = parent;
        _apiService = apiService;
        _parent.VpnService.OnStateChanged += OnVpnStateChanged;
        IsEditingAllowed = _parent.VpnService.CurrentState == AppVpnState.Disconnected;
    }

    //отписка от событий при выгрузке страницы
    private void OnUnloaded(object? sender, EventArgs e) => _parent?.VpnService.OnStateChanged -= OnVpnStateChanged;

    //блокировка настроек во время активного подключения vpn
    private void OnVpnStateChanged(AppVpnState s) =>
        MainThread.BeginInvokeOnMainThread(() => IsEditingAllowed = s == AppVpnState.Disconnected);

    //каскадная анимация появления карточек профиля
    public async Task PlayEntranceAnimationAsync()
    {
        Opacity = 1;
        TranslationY = 0;
        await this.PlayCardsEntranceAsync(35, 240);
    }

    //запрос персонального промокода пользователя с бэкенда
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
        catch (Exception ex)
        {
            AppLogger.LogError($"[PROFILE REFERRAL CODE ERROR] {ex.Message}", ex);
        }
    }

    //копирование персонального промокода в буфер обмена
    private async void OnCopyReferralCodeClickedAsync(object? sender, TappedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_myReferralCode))
        {
            await LoadReferralCodeAsync();
        }

        if (!string.IsNullOrWhiteSpace(_myReferralCode))
        {
            await Clipboard.Default.SetTextAsync(_myReferralCode);
            await _parent.DisplayAlertAsync(AccountMessages.ReferralCopiedTitle, AccountMessages.ReferralCopiedMessage(_myReferralCode), AccountMessages.ButtonOk);
        }
    }

    //активация введенного промокода или кода приглашения
    private async void OnActivatePromoClickedAsync(object? sender, EventArgs e)
    {
        if (_apiService is null)
        {
            return;
        }

        var code = PromoCodeEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            await _parent.DisplayAlertAsync(AccountMessages.AttentionTitle, AccountMessages.ReferralCodePrompt, AccountMessages.ButtonOk);
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
                await _parent.DisplayAlertAsync(AccountMessages.SuccessTitle, data?.Message ?? AccountMessages.PromoActivatedSuccess, AccountMessages.ButtonOk);
            }
            else
            {
                await _parent.DisplayAlertAsync(AccountMessages.ErrorTitle, ApiErrorHandler.ParseGeneralError(error, AccountMessages.PromoActivateFailed), AccountMessages.ButtonOk);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[ACTIVATE PROMO ERROR] {ex.Message}", ex);
            await _parent.DisplayAlertAsync(AccountMessages.ErrorTitle, ApiErrorHandler.ParseGeneralError(ex.Message, AccountMessages.PromoActivateFailed), AccountMessages.ButtonOk);
        }
        finally
        {
            await UIAnimations.SetButtonLoadingAsync(BtnActivatePromoBorder, BtnActivatePromo, LoaderActivatePromo, false);
        }
    }

    //отображение почты, провайдера и даты окончания подписки
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

    //форматирование остатка секунд баланса
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

    //применение цветов активной темы к фону карточек
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

    //компенсация отступа под статусбар на мобильных устройствах
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

    //обновление карточек и баланса при смене темы оформления
    public void OnThemeChanged() =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateCardOpacity();
            UpdateBalance(_lastRemainingSeconds);
        });

    //клик по кнопке пополнения баланса
    private void OnBuyTokensClicked(object? sender, EventArgs e) =>
        BuyTokensRequested?.Invoke(this, EventArgs.Empty);

    //переход к экрану удаления профиля
    private void OnDeleteAccountClicked(object? sender, EventArgs e) =>
        _ = _parent.SwitchTabAsync("delete");

    //выход из текущей учетной записи
    private void OnLogoutClicked(object? sender, EventArgs e) =>
        LogoutRequested?.Invoke(this, EventArgs.Empty);

    //переключение adblock dns фильтрации
    private void OnAdBlockToggled(object? sender, ToggledEventArgs e)
    {
        if (!IsEditingAllowed)
        {
            AdBlockSwitch.IsToggled = !e.Value;
            return;
        }

        Preferences.Default.Set(AppPreferenceKeys.UseAdblockDns, e.Value);
        DnsAdBlocker.IsAdBlockEnabled = e.Value;
        UpdateAdBlockSubtitle();
    }

    //обновление подписи со статистикой заблокированных трекеров
    private void UpdateAdBlockSubtitle()
    {
        if (!DnsAdBlocker.IsAdBlockEnabled)
        {
            AdBlockSubtitleLabel.Text = AccountMessages.AdBlockDisabled;
            return;
        }

        var blocked = DnsAdBlocker.BlockedQueriesCount;
        var rules = DnsAdBlocker.BlockedDomainsCount;
        AdBlockSubtitleLabel.Text = blocked > 0
            ? $"L3 фильтрация активна ({rules:N0} правил, заблокировано: {blocked:N0})"
            : $"L3 фильтрация активна (активных правил: {rules:N0})";
    }

    //сохранение настройки сбора анонимной телеметрии
    private void OnTelemetryToggled(object? sender, ToggledEventArgs e) =>
        Preferences.Default.Set(AppPreferenceKeys.UseTelemetry, e.Value);
}
