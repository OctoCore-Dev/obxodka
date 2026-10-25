namespace obxodka.Views;

public sealed partial class AuthView : ContentView
{
    private MainPage _parent = null!;
    private ApiService _apiService = null!;
    private bool _isVerifying;

    public AuthView()
    {
        InitializeComponent();
        UpdateSeasonalLogo();
    }

    public void Initialize(MainPage parent, ApiService apiService)
    {
        _parent = parent;
        _apiService = apiService;
        UpdateSeasonalLogo();
    }

    public void UpdateSeasonalLogo()
    {
        var isHalloween = SeasonSchedule.IsHalloweenSeason() ||
                          ThemeManager.Instance?.ActiveTheme?.Id?.Contains("halloween", StringComparison.OrdinalIgnoreCase) == true;
        AppLogoImage.Source = isHalloween ? "app_logo_halloween.png" : "app_logo.png";
    }

    public async Task PlayEntranceAnimationAsync()
    {
        await UIAnimations.PlayEntranceFadeScaleAsync(FormContainer);
        _ = CheckClipboardForLoginAsync();
    }

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
        catch { }
    }

    public void UpdateCardOpacity()
    {
        var bgSurface = (Application.Current?.Resources.TryGetValue("BgSurface", out var bg) == true && bg is Color bgColor)
            ? bgColor
            : Color.FromArgb("#161622");

        FormContainer.BackgroundColor = bgSurface;
    }

    public void SetHeaderTopInset(double top)
    {
        if (DeviceInfo.Idiom == DeviceIdiom.Phone && ContentLayout != null)
        {
            ContentLayout.Padding = new Thickness(24, Math.Max(top + 16, 48), 24, 48);
        }
    }

    public void OnThemeChanged() =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateCardOpacity();
            UpdateSeasonalLogo();
        });

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

    private async void OnScanQrCodeClickedAsync(object? sender, EventArgs e)
    {
        if (_isVerifying)
        {
            return;
        }

        _ = UIAnimations.HideErrorLabelAsync(AuthErrorLabel);

        try
        {
            var qrScanner = IPlatformApplication.Current?.Services?.GetService<IQrScannerService>();
            if (qrScanner == null || !qrScanner.IsSupported)
            {
                await ShowLoginErrorAsync("Сканирование QR-кода камерой поддерживается на Android. На ПК используйте 6-значный код или вставьте ссылку из буфера.");
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
                await ShowLoginErrorAsync("QR-код не содержит данных авторизации Обходка VPN.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[QR SCAN ERROR] {ex.Message}");
            await ShowLoginErrorAsync(string.IsNullOrWhiteSpace(ex.Message) ? "Не удалось отсканировать QR-код." : ex.Message);
        }
    }

    private async void OnPasteCodeClickedAsync(object? sender, EventArgs e)
    {
        try
        {
            if (!Clipboard.Default.HasText)
            {
                await ShowLoginErrorAsync("Буфер обмена пуст.");
                return;
            }

            var clipText = (await Clipboard.Default.GetTextAsync())?.Trim();
            if (string.IsNullOrWhiteSpace(clipText))
            {
                await ShowLoginErrorAsync("Буфер обмена пуст.");
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

            await ShowLoginErrorAsync("В буфере обмена не найден 6-значный код.");
        }
        catch
        {
            await ShowLoginErrorAsync("Не удалось прочитать буфер обмена.");
        }
    }

    private void OnCodeEntryTextChanged(object? sender, TextChangedEventArgs e)
    {
        var newText = e.NewTextValue?.Trim();
        if (newText is { Length: 6 } && newText.All(char.IsAsciiDigit) && !_isVerifying)
        {
            _ = VerifyCodeInternalAsync();
        }
    }

    private async void OnGetCodeClickedAsync(object? sender, EventArgs? e)
    {
        EmailEntry.Unfocus();
        var email = EmailEntry.Text?.Trim();

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            await ShowLoginErrorAsync("Пожалуйста, введите корректный Email.");
            return;
        }

        SetGetCodeLoadingState(true);
        _ = UIAnimations.HideErrorLabelAsync(AuthErrorLabel);

        try
        {
            var (success, error) = await _apiService.RequestCodeAsync(new EmailAuthRequest(email));
            if (success)
            {
                await UIAnimations.CrossFadeFormAsync(EmailInputView, CodeInputView);
                _ = CodeEntry.Focus();
            }
            else
            {
                await ShowLoginErrorAsync(ApiErrorHandler.ParseLoginError(error));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EMAIL AUTH ERROR] {ex.Message}");
            await ShowLoginErrorAsync(ApiErrorHandler.ParseGeneralError(ex.Message, "Ошибка соединения с сервером."));
        }
        finally
        {
            SetGetCodeLoadingState(false);
        }
    }

    private async void OnVerifyCodeClickedAsync(object? sender, EventArgs? e) =>
        await VerifyCodeInternalAsync();

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
            await ShowLoginErrorAsync("Пожалуйста, введите 6-значный код.");
            return;
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            await ShowLoginErrorAsync("Email не указан. Вернитесь назад.");
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
            Debug.WriteLine($"[VERIFY CODE ERROR] {ex.Message}");
            await ShowLoginErrorAsync(ApiErrorHandler.ParseGeneralError(ex.Message, "Не удалось проверить код."));
        }
        finally
        {
            _isVerifying = false;
            SetVerifyCodeLoadingState(false);
        }
    }

    private async void OnBackToEmailClickedAsync(object? sender, EventArgs? e)
    {
        _ = UIAnimations.HideErrorLabelAsync(AuthErrorLabel);
        CodeEntry.Text = string.Empty;
        await UIAnimations.CrossFadeFormAsync(CodeInputView, EmailInputView);
        _ = EmailEntry.Focus();
    }

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

    private void SetGetCodeLoadingState(bool isLoading)
    {
        if (GetCodeButton is not null)
        {
            GetCodeButton.IsEnabled = !isLoading;
            GetCodeButton.Text = isLoading ? string.Empty : "Получить код";
        }

        GetCodeLoader?.IsVisible = isLoading;
    }

    private void SetVerifyCodeLoadingState(bool isLoading)
    {
        if (VerifyCodeButton is not null)
        {
            VerifyCodeButton.IsEnabled = !isLoading;
            VerifyCodeButton.Text = isLoading ? string.Empty : "Войти";
        }

        VerifyCodeLoader?.IsVisible = isLoading;
    }

    private async Task ShowLoginErrorAsync(string msg)
    {
        AuthErrorLabel.Text = msg;
        await UIAnimations.ShowErrorLabelAsync(AuthErrorLabel);
        await AuthErrorLabel.ShakeErrorAsync();
    }
}
