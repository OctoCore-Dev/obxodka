namespace obxodka.Pages;

//экран входа по email, qr коду и deep link
public sealed partial class AuthView : ContentView
{
    private MainPage _parent = null!;
    private ApiService _apiService = null!;
    private IQrScannerService? _qrScanner;
    private bool _isVerifying;

    public AuthView()
    {
        InitializeComponent();
        UpdateSeasonalLogo();
    }

    //инициализация зависимостей экрана авторизации
    public void Initialize(MainPage parent, ApiService apiService, IQrScannerService? qrScanner = null)
    {
        _parent = parent;
        _apiService = apiService;
        _qrScanner = qrScanner ?? IPlatformApplication.Current?.Services?.GetService<IQrScannerService>();
        UpdateSeasonalLogo();
    }

    //обновление сезонного логотипа приложения
    public void UpdateSeasonalLogo()
    {
        var isHalloween = ThemeManager.Instance?.ActiveTheme?.Id?.Contains("halloween", StringComparison.OrdinalIgnoreCase) == true;
        AppLogoImage.Source = isHalloween ? "app_logo_halloween.png" : "app_logo.png";
    }

    //анимация плавного появления формы
    public async Task PlayEntranceAnimationAsync()
    {
        await UIAnimations.PlayEntranceFadeScaleAsync(FormContainer);
        _ = CheckClipboardForLoginAsync();
    }

    //проверка буфера обмена на наличие deep link ссылки авторизации
    public async Task CheckClipboardForLoginAsync()
    {
        if (!OperatingSystem.IsAndroid() || _isVerifying)
        {
            return;
        }

        try
        {
            if (!Clipboard.Default.HasText)
            {
                return;
            }

            var clip = (await Clipboard.Default.GetTextAsync())?.Trim();
            if (!string.IsNullOrWhiteSpace(clip) && App.TryParseDeepLinkLogin(clip, out var email, out var code))
            {
                await HandleDeepLinkLoginAsync(email, code);
            }
        }
        catch (Exception ex)
        {
            //игнорируем ошибки доступа к буферу обмена без фокуса окна
            AppLogger.Log($"[AUTH CLIPBOARD CHECK] {ex.Message}");
        }
    }

    //актуализация цвета фона карточки формы
    public void UpdateCardOpacity()
    {
        var bgSurface = (Application.Current?.Resources.TryGetValue("BgSurface", out var bg) == true && bg is Color bgColor)
            ? bgColor
            : Color.FromArgb("#161622");

        FormContainer.BackgroundColor = bgSurface;
    }

    //отступ под системный статусбар для мобильных устройств
    public void SetHeaderTopInset(double top)
    {
        if (DeviceInfo.Idiom == DeviceIdiom.Phone && ContentLayout != null)
        {
            ContentLayout.Padding = new Thickness(24, Math.Max(top + 16, 48), 24, 48);
        }
    }

    //реакция на смену темы оформления
    public void OnThemeChanged() =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateCardOpacity();
            UpdateSeasonalLogo();
        });

    //обработка входа по ссылке быстрого логина
    public async Task HandleDeepLinkLoginAsync(string email, string code)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        _ = UIAnimations.HideErrorLabelAsync(AuthErrorLabel);
        EmailEntry.Text = email.Trim();

        if (!CodeInputView.IsVisible)
        {
            EmailInputView.IsVisible = false;
            EmailInputView.Opacity = 0;
            CodeInputView.IsVisible = true;
            CodeInputView.Opacity = 1;
        }

        CodeEntry.Text = code.Trim();
        await VerifyCodeInternalAsync();
    }

    //сканирование qr кода камерой
    private async void OnScanQrCodeClickedAsync(object? sender, EventArgs e)
    {
        if (_isVerifying)
        {
            return;
        }

        _ = UIAnimations.HideErrorLabelAsync(AuthErrorLabel);

        try
        {
            var qrScanner = _qrScanner ?? IPlatformApplication.Current?.Services?.GetService<IQrScannerService>();
            if (qrScanner == null || !qrScanner.IsSupported)
            {
                await ShowLoginErrorAsync(AuthMessages.QrDesktopHint);
                return;
            }

            var scannedData = await qrScanner.ScanQrCodeAsync();
            if (string.IsNullOrWhiteSpace(scannedData))
            {
                return;
            }

            if (App.TryParseDeepLinkLogin(scannedData, out var email, out var code))
            {
                await HandleDeepLinkLoginAsync(email, code);
            }
            else
            {
                await ShowLoginErrorAsync(AuthMessages.QrInvalidPayload);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[QR SCAN ERROR] {ex.Message}", ex);
            await ShowLoginErrorAsync(ApiErrorHandler.ParseGeneralError(ex.Message, AuthMessages.QrScanFailed));
        }
    }

    //вставка 6-значного кода из буфера обмена
    private async void OnPasteCodeClickedAsync(object? sender, EventArgs e)
    {
        try
        {
            if (!Clipboard.Default.HasText)
            {
                await ShowLoginErrorAsync(AuthMessages.ClipboardEmpty);
                return;
            }

            var clipText = (await Clipboard.Default.GetTextAsync())?.Trim();
            if (string.IsNullOrWhiteSpace(clipText))
            {
                await ShowLoginErrorAsync(AuthMessages.ClipboardEmpty);
                return;
            }

            if (App.TryParseDeepLinkLogin(clipText, out var qrEmail, out var qrCode))
            {
                await HandleDeepLinkLoginAsync(qrEmail, qrCode);
                return;
            }

            var digits = new string([.. clipText.Where(char.IsAsciiDigit)]);
            if (digits.Length >= 6)
            {
                CodeEntry.Text = digits[..6];
                return;
            }

            await ShowLoginErrorAsync(AuthMessages.ClipboardNoCode);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[PASTE CODE ERROR] {ex.Message}", ex);
            await ShowLoginErrorAsync(AuthMessages.ClipboardReadFailed);
        }
    }

    //реакция на ввод текста в поле email с автодополнением доменов при вводе '@'
    private void OnEmailEntryTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (AuthErrorLabel.IsVisible)
        {
            _ = UIAnimations.HideErrorLabelAsync(AuthErrorLabel);
        }

        var text = e.NewTextValue?.Trim() ?? string.Empty;
        var atIndex = text.IndexOf('@');
        if (atIndex < 0)
        {
            _ = UIAnimations.HideSuggestionsDropdownAsync(DomainSuggestionsCard);
            return;
        }

        var localPart = text[..atIndex];
        var query = text[(atIndex + 1)..];

        if (string.IsNullOrWhiteSpace(localPart))
        {
            _ = UIAnimations.HideSuggestionsDropdownAsync(DomainSuggestionsCard);
            return;
        }

        var matches = EmailValidator.PopularDomains
            .Where(d => string.IsNullOrEmpty(query) || d.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .Take(4)
            .ToList();

        if (matches.Count == 0 || (matches.Count == 1 && string.Equals(query, matches[0], StringComparison.OrdinalIgnoreCase)))
        {
            _ = UIAnimations.HideSuggestionsDropdownAsync(DomainSuggestionsCard);
            return;
        }

        DomainSuggestionsList.Children.Clear();
        foreach (var domain in matches)
        {
            var fullEmail = $"{localPart}@{domain}";
            var btn = new Button
            {
                Text = fullEmail,
                BackgroundColor = Colors.Transparent,
                TextColor = (Application.Current?.Resources.TryGetValue("TextPrimary", out var tp) == true && tp is Color tColor) ? tColor : Colors.White,
                FontFamily = (Application.Current?.Resources.TryGetValue("AppFontMedium", out var fm) == true && fm is string fStr) ? fStr : null,
                FontSize = 13,
                HeightRequest = 34,
                HorizontalOptions = LayoutOptions.Fill,
                Padding = new Thickness(12, 0),
                CornerRadius = 6,
                BorderWidth = 0
            };

            btn.Clicked += async (s, ev) =>
            {
                await UIAnimations.HideSuggestionsDropdownAsync(DomainSuggestionsCard);
                EmailEntry.Text = fullEmail;
                EmailEntry.CursorPosition = fullEmail.Length;
                _ = EmailEntry.Focus();
            };

            DomainSuggestionsList.Children.Add(btn);
        }

        if (!DomainSuggestionsCard.IsVisible)
        {
            _ = UIAnimations.ShowSuggestionsDropdownAsync(DomainSuggestionsCard);
        }
    }

    //автоматическая верификация при заполнении всех 6 цифр
    private void OnCodeEntryTextChanged(object? sender, TextChangedEventArgs e)
    {
        var newText = e.NewTextValue?.Trim();
        if (newText is { Length: 6 } && newText.All(char.IsAsciiDigit) && !_isVerifying)
        {
            _ = VerifyCodeInternalAsync();
        }
    }

    //запрос одноразового кода на почту
    private async void OnGetCodeClickedAsync(object? sender, EventArgs? e)
    {
        EmailEntry.Unfocus();
        _ = UIAnimations.HideSuggestionsDropdownAsync(DomainSuggestionsCard);
        var rawEmail = EmailEntry.Text?.Trim();

        var validation = EmailValidator.Validate(rawEmail);
        if (!validation.IsValid)
        {
            if (!string.IsNullOrEmpty(validation.SuggestedCorrection))
            {
                var fixConfirmed = await _parent.ShowCustomDialogAsync(
                    "Возможная опечатка",
                    $"В адресе обнаружена ошибка:\n{rawEmail}\n\nВозможно, имелось в виду:\n{validation.SuggestedCorrection}?\n\nИсправить адрес?",
                    acceptText: "Исправить",
                    cancelText: "Отмена");

                if (fixConfirmed)
                {
                    EmailEntry.Text = validation.SuggestedCorrection;
                    EmailEntry.CursorPosition = validation.SuggestedCorrection.Length;
                    _ = EmailEntry.Focus();
                }
                return;
            }

            _ = await _parent.ShowCustomDialogAsync("Неверный Email", validation.ErrorMessage ?? AuthMessages.EnterValidEmail);
            return;
        }

        var email = validation.NormalizedEmail ?? rawEmail!;
        SetGetCodeLoadingState(true);
        _ = UIAnimations.HideErrorLabelAsync(AuthErrorLabel);

        try
        {
            var (success, error) = await _apiService.RequestCodeAsync(new EmailAuthRequest(email));
            if (success)
            {
                CodeSentTargetLabel.Text = $"Код отправлен на {email}.\nВведите 6-значный код из письма:";
                await UIAnimations.CrossFadeFormAsync(EmailInputView, CodeInputView);
                _ = CodeEntry.Focus();
            }
            else
            {
                var errorMsg = ApiErrorHandler.ParseLoginError(error);
                _ = await _parent.ShowCustomDialogAsync("Ошибка отправки", errorMsg);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[EMAIL AUTH ERROR] {ex.Message}", ex);
            var generalError = ApiErrorHandler.ParseGeneralError(ex.Message, AuthMessages.ConnectionError);
            _ = await _parent.ShowCustomDialogAsync("Сбой подключения", generalError);
        }
        finally
        {
            SetGetCodeLoadingState(false);
        }
    }

    //нажатие кнопки подтверждения кода
    private async void OnVerifyCodeClickedAsync(object? sender, EventArgs? e) =>
        await VerifyCodeInternalAsync();

    //проверка введенного кода на сервере
    private async Task VerifyCodeInternalAsync()
    {
        if (_isVerifying)
        {
            return;
        }

        CodeEntry.Unfocus();

        var code = CodeEntry.Text?.Trim();
        var email = EmailEntry.Text?.Trim();

        if (string.IsNullOrWhiteSpace(code) || code.Length < 6)
        {
            await ShowLoginErrorAsync(AuthMessages.EnterSixDigitCode);
            return;
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            await ShowLoginErrorAsync(AuthMessages.EmailRequired);
            return;
        }

        _isVerifying = true;
        SetVerifyCodeLoadingState(true);
        _ = UIAnimations.HideErrorLabelAsync(AuthErrorLabel);

        try
        {
            var request = new EmailVerifyRequest(email, code, DeviceHelper.Hwid, DeviceHelper.DeviceName);
            var (success, data, error) = await _apiService.VerifyCodeAsync(request);

            if (success && data is not null)
            {
                await CompleteLoginAsync(data);
            }
            else
            {
                await ShowLoginErrorAsync(ApiErrorHandler.ParseLoginError(error));
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[VERIFY CODE ERROR] {ex.Message}", ex);
            await ShowLoginErrorAsync(ApiErrorHandler.ParseGeneralError(ex.Message, AuthMessages.CodeVerificationFailed));
        }
        finally
        {
            _isVerifying = false;
            SetVerifyCodeLoadingState(false);
        }
    }

    //возврат к экрану ввода адреса электронной почты
    private async void OnBackToEmailClickedAsync(object? sender, EventArgs? e)
    {
        _ = UIAnimations.HideErrorLabelAsync(AuthErrorLabel);
        CodeEntry.Text = string.Empty;
        await UIAnimations.CrossFadeFormAsync(CodeInputView, EmailInputView);
        _ = EmailEntry.Focus();
    }

    //сохранение сессии в защищенное хранилище и переключение в приложение
    private async Task CompleteLoginAsync(LoginResponse data)
    {
        await AuthManager.SaveSessionAsync(new UserSession
        {
            Email = string.IsNullOrEmpty(data.Email) ? EmailEntry.Text?.Trim() : data.Email,
            Password = "email_otp",
            JwtToken = data.Token,
            VpnConfig = data.VpnConfig,
            SubscriptionUntil = data.SubscriptionUntil,
            BalanceSeconds = data.BalanceSeconds,
            IsLoggedIn = true
        });

        await _parent.SwitchToAppAfterAuthAsync();
    }

    //управление индикатором загрузки при отправке кода
    private void SetGetCodeLoadingState(bool isLoading)
    {
        if (GetCodeButton is not null)
        {
            GetCodeButton.IsEnabled = !isLoading;
            GetCodeButton.Text = isLoading ? string.Empty : AuthMessages.GetCodeButton;
        }

        GetCodeLoader?.IsVisible = isLoading;
    }

    //управление индикатором загрузки при проверке кода
    private void SetVerifyCodeLoadingState(bool isLoading)
    {
        if (VerifyCodeButton is not null)
        {
            VerifyCodeButton.IsEnabled = !isLoading;
            VerifyCodeButton.Text = isLoading ? string.Empty : AuthMessages.VerifyCodeButton;
        }

        VerifyCodeLoader?.IsVisible = isLoading;
    }

    //отображение сообщения об ошибке с анимацией тряски и диалогом NeoDialog
    private async Task ShowLoginErrorAsync(string msg)
    {
        AuthErrorLabel.Text = msg;
        await UIAnimations.ShowErrorLabelAsync(AuthErrorLabel);
        await AuthErrorLabel.ShakeErrorAsync();
        _ = await _parent.ShowCustomDialogAsync("Ошибка", msg);
    }
}
