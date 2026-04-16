namespace obxodka.Pages;
internal partial class LoginPage : ContentPage
{
    private bool _isPasswordVisible;
    private readonly ApiService _apiService;
    public LoginPage(ApiService apiService)
    {
        InitializeComponent();
        _apiService = apiService;
    }
    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = PlayPageAnimationAsync();
    }
    private async Task PlayPageAnimationAsync()
    {
        AppIcon.Opacity = 0; AppIcon.Scale = 0.5; AppIcon.TranslationY = -20;
        TitleLabel.Opacity = 0; TitleLabel.TranslationY = 20;
        FormContainer.Opacity = 0; FormContainer.Scale = 0.9;
        LoginButtonBorder.Opacity = 0; LoginButtonBorder.TranslationY = 30;
        RegisterLabel.Opacity = 0;
        _ = AppIcon.FadeToAsync(1, 600);
        _ = AppIcon.TranslateToAsync(0, 0, 600, Easing.CubicOut);
        _ = AppIcon.ScaleToAsync(1, 800, Easing.SpringOut);
        await Task.Delay(100);
        _ = TitleLabel.FadeToAsync(1, 400);
        _ = TitleLabel.TranslateToAsync(0, 0, 400, Easing.CubicOut);
        await Task.Delay(100);
        _ = FormContainer.FadeToAsync(1, 500);
        _ = FormContainer.ScaleToAsync(1, 500, Easing.SpringOut);
        await Task.Delay(100);
        _ = LoginButtonBorder.FadeToAsync(1, 400);
        _ = LoginButtonBorder.TranslateToAsync(0, 0, 400, Easing.CubicOut);
        await Task.Delay(100);
        _ = RegisterLabel.FadeToAsync(0.8, 600);
    }
    private void OnPasswordEyeClicked(object? sender, EventArgs? e)
    {
        _isPasswordVisible = !_isPasswordVisible;
        PasswordEntry.IsPassword = !_isPasswordVisible;
        EyeImg.Source = _isPasswordVisible ? "eye_icon.png" : "eye_off.png";
    }
    private async void OnLoginClicked(object? sender, EventArgs? e)
    {
        string email = EmailEntry.Text?.Trim() ?? string.Empty;
        string password = PasswordEntry.Text;
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            await ShowErrorAsync("Заполните все поля").ConfigureAwait(true);
            return;
        }
        _ = LoginButtonBorder.ScaleToAsync(0.95, 100).ContinueWith(t => LoginButtonBorder.ScaleToAsync(1.0, 100));
        SetLoadingState(true);
        _ = CommonErrorLabel.FadeToAsync(0, 100);
        try
        {
            var (success, data, error) = await _apiService.LoginAsync(new AuthRequest
            {
                Email = email,
                Password = password,
                Hwid = DeviceHelper.GetHwid(),
                DeviceName = DeviceInfo.Name
            }).ConfigureAwait(true);
            if (success && data is not null)
            {
                await AuthManager.SaveSessionAsync(new UserSession
                {
                    Email = email,
                    Password = password,
                    JwtToken = data.Token,
                    IsLoggedIn = true
                }).ConfigureAwait(true);
                NavigateToMainPage();
            }
            else
            {
                string friendlyMessage = "Неверная почта или пароль";
                if (!string.IsNullOrEmpty(error))
                {
                    if (error.Contains("Unauthorized") || error.Contains("401") || error.Contains("Invalid"))
                    {
                        friendlyMessage = "Аккаунт не найден или пароль неверный";
                    }
                    else if (error.Contains("limit") || error.Contains("device"))
                    {
                        friendlyMessage = "Лимит устройств исчерпан (макс. 3)";
                    }
                    else if (error.Contains("banned") || error.Contains("blocked"))
                    {
                        friendlyMessage = "Ваш аккаунт заблокирован";
                    }
                    else
                    {
                        friendlyMessage = error;
                    }
                }
                await ShowErrorAsync(friendlyMessage).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LOGIN ERROR] {ex.Message}");
            await ShowErrorAsync("Сервер временно недоступен").ConfigureAwait(true);
        }
        finally
        {
            SetLoadingState(false);
        }
    }
    private void NavigateToMainPage()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var mainPage = Handler?.MauiContext?.Services.GetRequiredService<MainPage>();
            if (mainPage is not null && Application.Current?.Windows.Count > 0)
            {
                Application.Current.Windows[0].Page = new NavigationPage(mainPage);
            }
        });
    }
    private async Task ShowErrorAsync(string msg)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            CommonErrorLabel.Text = msg;
            _ = CommonErrorLabel.FadeToAsync(1, 200);
        });
        await ShakeAsync(FormContainer).ConfigureAwait(true);
    }
    private static async Task ShakeAsync(VisualElement element)
    {
        const uint duration = 50;
        const int offset = 10;
        for (int i = 0; i < 2; i++)
        {
            await element.TranslateToAsync(offset, 0, duration).ConfigureAwait(true);
            await element.TranslateToAsync(-offset, 0, duration).ConfigureAwait(true);
        }
        await element.TranslateToAsync(0, 0, duration).ConfigureAwait(true);
    }
    private void SetLoadingState(bool isLoading)
    {
        MainLoginButton.IsEnabled = !isLoading;
        MainLoginButton.Text = isLoading ? "Входим..." : "Войти";
        LoginButtonBorder.Opacity = isLoading ? 0.7 : 1.0;
    }
    private async void OnRegisterLabelTapped(object? sender, TappedEventArgs? e)
    {
        var registerPage = Handler?.MauiContext?.Services.GetRequiredService<RegisterPage>();
        if (registerPage is not null)
        {
            await Navigation.PushAsync(registerPage).ConfigureAwait(true);
        }
    }
}