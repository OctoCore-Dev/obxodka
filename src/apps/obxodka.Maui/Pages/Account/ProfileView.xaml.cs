namespace obxodka.Pages.Account;

//экран профиля пользователя, баланса, adblock и рефералов
public sealed partial class ProfileView : IAdaptivePage
{
    public static readonly BindableProperty IsEditingAllowedProperty =
        BindableProperty.Create(nameof(IsEditingAllowed), typeof(bool), typeof(ProfileView), true);

    private ApiService? _apiService;
    private bool? _isTwoColumns;
    private long _lastRemainingSeconds;
    private string? _myReferralCode;
    private MainPage? _parent;

    public ProfileView()
    {
        InitializeComponent();

        var isAdblock = Preferences.Default.Get(AppPreferenceKeys.UseAdblockDns, true);
        AdBlockSwitch.IsToggled = isAdblock;
        DnsAdBlocker.IsAdBlockEnabled = isAdblock;
        UpdateAdBlockSubtitle();
        TelemetrySwitch.IsToggled = Preferences.Default.Get(AppPreferenceKeys.UseTelemetry, true);

        Unloaded += OnUnloaded;
    }

    private static Color ErrorColor =>
        Application.Current?.Resources.TryGetValue("Error", out var val) == true && val is Color c ? c : Color.FromArgb("#EF4444");

    private static Color AccentColor =>
        Application.Current?.Resources.TryGetValue("Accent", out var val) == true && val is Color c ? c : Color.FromArgb("#00E5FF");

    public bool IsEditingAllowed
    {
        get => (bool)GetValue(IsEditingAllowedProperty);
        set => SetValue(IsEditingAllowedProperty, value);
    }

    //обновление карточек и баланса при смене темы оформления
    public void OnThemeChanged() =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateCardOpacity();
            UpdateBalance(_lastRemainingSeconds);
        });

    //отписка от событий при выгрузке страницы
    private void OnUnloaded(object? sender, EventArgs e) => _parent?.VpnService.OnStateChanged -= OnVpnStateChanged;

    //блокировка настроек во время активного подключения vpn
    private void OnVpnStateChanged(AppVpnState s) =>
        MainThread.BeginInvokeOnMainThread(() => IsEditingAllowed = s == AppVpnState.Disconnected);

    //сохранение настройки сбора анонимной телеметрии
    private void OnTelemetryToggled(object? sender, ToggledEventArgs e) =>
        Preferences.Default.Set(AppPreferenceKeys.UseTelemetry, e.Value);

    //клик по кнопке пополнения баланса
    private void OnBuyTokensClicked(object? sender, EventArgs e) =>
        BuyTokensRequested?.Invoke(this, EventArgs.Empty);

    //переход к экрану удаления профиля
    private void OnDeleteAccountClicked(object? sender, EventArgs e)
    {
        if (_parent is not null)
        {
            _ = _parent.SwitchTabAsync("delete");
        }
    }

    //выход из текущей учетной записи
    private void OnLogoutClicked(object? sender, EventArgs e) =>
        LogoutRequested?.Invoke(this, EventArgs.Empty);

    public event EventHandler? BuyTokensRequested;
    public event EventHandler? LogoutRequested;
    public event EventHandler? BalanceRefreshRequested;


    //единая адаптация экрана профиля по ssot-снимку геометрии окна без ручного widthrequest
    public void ApplyViewport(AdaptiveViewport viewport)
    {
        if (RootLayoutGrid is null || ContentStack is null || ProfileTopGrid is null)
        {
            return;
        }

        var isCompact = viewport.IsCompact;
        var baseSidePad = isCompact ? 16.0 : viewport.IsExpanded ? 28.0 : 20.0;
        var sidePad = viewport.ComputeHorizontalPadding(baseSidePad, 960.0);
        var topPad = DeviceInfo.Platform == DevicePlatform.Android || DeviceInfo.Platform == DevicePlatform.iOS
            ? Math.Max(viewport.SafeTop + 14.0, isCompact ? 42.0 : 24.0)
            : isCompact ? 12.0 : 16.0;
        var bottomPad = !viewport.UseSidebar
            ? Math.Max(116.0, viewport.SafeBottom + 98.0)
            : Math.Max(32.0, viewport.SafeBottom + 20.0);

        RootLayoutGrid.Padding = new Thickness(sidePad, 0, sidePad, 0);
        ContentStack.Padding = new Thickness(0, topPad, 0, bottomPad);
        var showMobileLogout = !viewport.UseSidebar;
        CardLogout.IsVisible = showMobileLogout;
        CardLogout.Margin = showMobileLogout ? new Thickness(0, 0, 0, 14) : new Thickness(0);

        var isTwoColumns = viewport.UseMultiColumnContent;
        if (_isTwoColumns == isTwoColumns)
        {
            return;
        }

        _isTwoColumns = isTwoColumns;
        ProfileTopGrid.ColumnDefinitions.Clear();
        ProfileTopGrid.RowDefinitions.Clear();
        if (isTwoColumns)
        {
            ProfileTopGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            ProfileTopGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            ProfileTopGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            ProfileTopGrid.ColumnSpacing = 16;
            ProfileTopGrid.RowSpacing = 0;

            Grid.SetRow(CardProfile, 0);
            Grid.SetColumn(CardProfile, 0);
            Grid.SetRow(CardBalance, 0);
            Grid.SetColumn(CardBalance, 1);
        }
        else
        {
            ProfileTopGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            ProfileTopGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            ProfileTopGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            ProfileTopGrid.ColumnSpacing = 0;
            ProfileTopGrid.RowSpacing = 14;

            Grid.SetRow(CardProfile, 0);
            Grid.SetColumn(CardProfile, 0);
            Grid.SetRow(CardBalance, 1);
            Grid.SetColumn(CardBalance, 0);
        }
    }

    //инициализация зависимостей и подписка на статус vpn
    public void Initialize(MainPage parent, ThemeManager? _ = null, ApiService? apiService = null)
    {
        _parent?.VpnService.OnStateChanged -= OnVpnStateChanged;

        _parent = parent;
        _apiService = apiService;
        parent.VpnService.OnStateChanged += OnVpnStateChanged;
        IsEditingAllowed = parent.VpnService.CurrentState == AppVpnState.Disconnected;
    }

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
        try
        {
            if (string.IsNullOrWhiteSpace(_myReferralCode))
            {
                await LoadReferralCodeAsync();
            }

            if (string.IsNullOrWhiteSpace(_myReferralCode))
            {
                return;
            }

            await Clipboard.Default.SetTextAsync(_myReferralCode);
            if (_parent is not null)
            {
                await _parent.DisplayAlertAsync(AccountMessages.ReferralCopiedTitle, AccountMessages.ReferralCopiedMessage(_myReferralCode),
                    AccountMessages.ButtonOk);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[COPY REFERRAL ERROR] {ex.Message}", ex);
        }
    }

    //активация введенного промокода или кода приглашения
    private async void OnActivatePromoClickedAsync(object? sender, EventArgs e)
    {
        try
        {
            if (_apiService is null || _parent is null)
            {
                return;
            }

            var code = PromoCodeEntry.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(code))
            {
                await _parent.DisplayAlertAsync(AccountMessages.AttentionTitle, AccountMessages.ReferralCodePrompt,
                    AccountMessages.ButtonOk);
                return;
            }

            await UiAnimations.SetButtonLoadingAsync(BtnActivatePromoBorder, BtnActivatePromo, LoaderActivatePromo, true);
            try
            {
                var (success, data, error) = await _apiService.ActivateReferralCodeAsync(code);
                if (success)
                {
                    PromoCodeEntry.Text = string.Empty;
                    BalanceRefreshRequested?.Invoke(this, EventArgs.Empty);
                    await _parent.DisplayAlertAsync(AccountMessages.SuccessTitle, data?.Message ?? AccountMessages.PromoActivatedSuccess,
                        AccountMessages.ButtonOk);
                }
                else
                {
                    await _parent.DisplayAlertAsync(AccountMessages.ErrorTitle,
                        ApiErrorHandler.ParseGeneralError(error, AccountMessages.PromoActivateFailed), AccountMessages.ButtonOk);
                }
            }
            finally
            {
                await UiAnimations.SetButtonLoadingAsync(BtnActivatePromoBorder, BtnActivatePromo, LoaderActivatePromo, false);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[ACTIVATE PROMO ERROR] {ex.Message}", ex);
            if (_parent is not null)
            {
                await _parent.DisplayAlertAsync(AccountMessages.ErrorTitle,
                    ApiErrorHandler.ParseGeneralError(ex.Message, AccountMessages.PromoActivateFailed), AccountMessages.ButtonOk);
            }
        }
    }

    //отображение почты, провайдера и даты окончания подписки
    public void UpdateProfileInfo(UserSession session)
    {
        ProfileEmailLabel.Text = session.Email ?? "Unknown Email";

        var providerName = "EMAIL";
        var iconSource = "email_logo.png";

        if (session.Email is { Length: > 0 } email && email.IndexOf('@') is var atIdx and >= 0)
        {
            var domain = email.AsSpan(atIdx + 1);

            if (domain.Equals("gmail.com", StringComparison.OrdinalIgnoreCase))
            {
                providerName = "GOOGLE";
                iconSource = "google_logo.png";
            }
            else if (domain.StartsWith("yandex.", StringComparison.OrdinalIgnoreCase) ||
                     domain.Equals("ya.ru", StringComparison.OrdinalIgnoreCase))
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
            ProfileTokenLabel.Text = TimeFormatHelper.FormatSeconds(remainingSeconds);
            ProfileTokenLabel.TextColor = AccentColor;
        }
    }

    //применение цветов активной темы к фону карточек
    public void UpdateCardOpacity()
    {
        var bgSurface = Application.Current?.Resources.TryGetValue("BgSurface", out var bg) == true && bg is Color bgColor
            ? bgColor
            : Color.FromArgb("#161622");
        var borderSubtle = Application.Current?.Resources.TryGetValue("BorderSubtle", out var bs) == true && bs is Color bsc
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

    //сброс кэшированных данных профиля при выходе из аккаунта
    public void ResetAccountCache()
    {
        _myReferralCode = string.Empty;
        MainThread.BeginInvokeOnMainThread(() => MyReferralCodeLabel.Text = "------");
    }

    private bool _isUpdatingAdBlockSwitch;

    //переключение adblock dns фильтрации
    private void OnAdBlockToggled(object? sender, ToggledEventArgs e)
    {
        if (_isUpdatingAdBlockSwitch)
        {
            return;
        }

        if (!IsEditingAllowed)
        {
            try
            {
                _isUpdatingAdBlockSwitch = true;
                AdBlockSwitch.IsToggled = !e.Value;
            }
            finally
            {
                _isUpdatingAdBlockSwitch = false;
            }

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
}
