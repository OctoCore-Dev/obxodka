namespace obxodka.Views;

public sealed partial class AuthView : ContentView
{
    private MainPage _parent = null!;
    private ApiService _apiService = null!;

    public AuthView() => InitializeComponent();

    public void Initialize(MainPage parent, ApiService apiService)
    {
        _parent = parent;
        _apiService = apiService;
    }

    public Task PlayEntranceAnimationAsync() =>
        UIAnimations.PlayEntranceFadeScaleAsync(FormContainer);

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

    private async void OnVerifyCodeClickedAsync(object? sender, EventArgs? e)
    {
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
