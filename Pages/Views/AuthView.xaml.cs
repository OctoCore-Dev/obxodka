namespace obxodka.Views;

public partial class AuthView : ContentView
{
    private MainPage _parent = null!;
    private ApiService _apiService = null!;

    public AuthView() => InitializeComponent();

    public void Initialize(MainPage parent, ApiService apiService)
    {
        _parent = parent;
        _apiService = apiService;
    }


#pragma warning disable CA1822
    public Task PlayEntranceAnimationAsync() => Task.CompletedTask;
#pragma warning restore CA1822

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
        AuthErrorLabel.IsVisible = false;

        try
        {
            var (success, error) = await _apiService.RequestCodeAsync(new EmailAuthRequest(email));
            if (success)
            {
                EmailInputView.IsVisible = false;
                CodeInputView.IsVisible = true;
                CodeInputView.Opacity = 0;
                _ = CodeInputView.FadeToAsync(1, 300, Easing.CubicOut);
                _ = CodeEntry.Focus();
            }
            else
            {
                await ShowLoginErrorAsync(error ?? "Ошибка при отправке кода.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EMAIL AUTH ERROR] {ex.Message}");
            await ShowLoginErrorAsync("Ошибка соединения с сервером.");
        }
        finally
        {
            SetGetCodeLoadingState(false);
        }
    }

    private async void OnVerifyCodeClickedAsync(object? sender, EventArgs? e)
    {
        EmailEntry.Unfocus();
        CodeEntry.Unfocus();

        var code = CodeEntry.Text?.Trim();
        var email = EmailEntry.Text?.Trim();

        if (string.IsNullOrWhiteSpace(code) || code.Length < 6)
        {
            await ShowLoginErrorAsync("Пожалуйста, введите 6-значный код.");
            return;
        }

        SetVerifyCodeLoadingState(true);
        AuthErrorLabel.IsVisible = false;

        try
        {
            var (success, data, error) = await _apiService.VerifyCodeAsync(new EmailVerifyRequest(email!, code, DeviceHelper.GetHwid(), DeviceHelper.GetDeviceName()));
            if (success && data != null)
            {
                await CompleteLoginAsync(data);
            }
            else
            {
                await ShowLoginErrorAsync(error ?? "Неверный код.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VERIFY CODE ERROR] {ex.Message}");
            await ShowLoginErrorAsync($"Ошибка: {ex.Message}");
        }
        finally
        {
            SetVerifyCodeLoadingState(false);
        }
    }

    private void OnBackToEmailClicked(object? sender, EventArgs? e)
    {
        AuthErrorLabel.IsVisible = false;
        CodeEntry.Text = string.Empty;
        CodeInputView.IsVisible = false;
        EmailInputView.IsVisible = true;
        EmailInputView.Opacity = 0;
        _ = EmailInputView.FadeToAsync(1, 300, Easing.CubicOut);
        _ = EmailEntry.Focus();
    }

    private async Task CompleteLoginAsync(LoginResponse data)
    {
        await AuthManager.SaveSessionAsync(new UserSession
        {
            Email = string.IsNullOrEmpty(data.Email) ? EmailEntry.Text.Trim() : data.Email,
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
            GetCodeButton.Text = isLoading ? "" : "Получить код";
        }
        _ = (GetCodeLoader?.IsVisible = isLoading);
    }

    private void SetVerifyCodeLoadingState(bool isLoading)
    {
        if (VerifyCodeButton is not null)
        {
            VerifyCodeButton.IsEnabled = !isLoading;
            VerifyCodeButton.Text = isLoading ? "" : "Войти";
        }
        _ = (VerifyCodeLoader?.IsVisible = isLoading);
    }

    private async Task ShowLoginErrorAsync(string msg)
    {
        AuthErrorLabel.Text = msg;
        AuthErrorLabel.IsVisible = true;
        AuthErrorLabel.TranslationY = -8;
        AuthErrorLabel.Opacity = 0;
        _ = await Task.WhenAll(
            AuthErrorLabel.FadeToAsync(1, 200, Easing.CubicOut),
            AuthErrorLabel.TranslateToAsync(0, 0, 200, Easing.CubicOut)
        );
    }
}
