namespace obxodka.Pages;
internal partial class RegisterPage : ContentPage
{
    private readonly ApiService _apiService;
    private bool _isPasswordVisible;
    private bool _isRepeatPasswordVisible;
    public RegisterPage(ApiService apiService)
    {
        InitializeComponent();
        _apiService = apiService;
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await PlayPageAnimationAsync().ConfigureAwait(true);
    }
    private async Task PlayPageAnimationAsync()
    {
        TitleLabel.Opacity = 0; TitleLabel.TranslationY = -20;
        FormContainer.Opacity = 0; FormContainer.Scale = 0.8;
        RegisterButtonBorder.Opacity = 0; RegisterButtonBorder.TranslationY = 30;
        _ = TitleLabel.FadeToAsync(1, 400);
        _ = TitleLabel.TranslateToAsync(0, 0, 400, Easing.CubicOut);
        await Task.Delay(100).ConfigureAwait(true);
        _ = FormContainer.FadeToAsync(1, 500);
        _ = FormContainer.ScaleToAsync(1, 500, Easing.SpringOut);
        await Task.Delay(100).ConfigureAwait(true);
        _ = RegisterButtonBorder.FadeToAsync(1, 500);
        _ = RegisterButtonBorder.TranslateToAsync(0, 0, 500, Easing.SpringOut);
    }
    private void TogglePasswordVisibility(object? sender, EventArgs? e)
    {
        _isPasswordVisible = !_isPasswordVisible;
        PasswordEntry.IsPassword = !_isPasswordVisible;
        EyeImg1.Source = _isPasswordVisible ? "eye_icon.png" : "eye_off.png";
    }
    private void ToggleRepeatPasswordVisibility(object? sender, EventArgs? e)
    {
        _isRepeatPasswordVisible = !_isRepeatPasswordVisible;
        PasswordRepeatEntry.IsPassword = !_isRepeatPasswordVisible;
        EyeImg2.Source = _isRepeatPasswordVisible ? "eye_icon.png" : "eye_off.png";
    }
    private async void OnPolicyLinkClick(object? sender, TappedEventArgs? e)
    {
        try { await Launcher.Default.OpenAsync("https://obxodka.one/Home/Privacy").ConfigureAwait(true); }
        catch { }
    }
    private async void OnRegisterClicked(object? sender, EventArgs? e)
    {
        if (PolicyCheckBox.IsChecked != true)
        {
            await ShowErrorAsync("Примите условия политики").ConfigureAwait(true);
            return;
        }
        string email = EmailEntry.Text?.Trim() ?? string.Empty;
        string pass = PasswordEntry.Text;
        string confirm = PasswordRepeatEntry.Text;
        if (string.IsNullOrEmpty(email)) { await ShowErrorAsync("Введите Email").ConfigureAwait(false); return; }
        if (string.IsNullOrEmpty(pass) || pass.Length < 6) { await ShowErrorAsync("Пароль от 6 символов").ConfigureAwait(false); return; }
        if (pass != confirm) { await ShowErrorAsync("Пароли не совпадают").ConfigureAwait(false); return; }
        _ = RegisterButtonBorder.ScaleToAsync(0.95, 100).ContinueWith(t => RegisterButtonBorder.ScaleToAsync(1.0, 100));
        SetLoadingState(true);
        _ = CommonErrorLabel.FadeToAsync(0, 100);
        try
        {
            var authRequest = new AuthRequest { Email = email, Password = pass, Hwid = DeviceHelper.GetHwid(), DeviceName = DeviceInfo.Name };
            var (regSuccess, regError) = await _apiService.RegisterAsync(authRequest).ConfigureAwait(true);
            if (regSuccess)
            {
                var (loginSuccess, loginData, _) = await _apiService.LoginAsync(authRequest).ConfigureAwait(true);
                if (loginSuccess && loginData is not null)
                {
                    await AuthManager.SaveSessionAsync(new UserSession { Email = email, Password = pass, JwtToken = loginData.Token, IsLoggedIn = true }).ConfigureAwait(true);
                    NavigateToMainPage();
                }
                else await ShowErrorAsync("Аккаунт создан, но войти не удалось.").ConfigureAwait(true);
            }
            else
            {
                bool isConflict = regError?.Contains("Conflict", StringComparison.OrdinalIgnoreCase) == true;
                bool isBadRequest = regError?.Contains("BadRequest", StringComparison.OrdinalIgnoreCase) == true;
                await ShowErrorAsync(isConflict || isBadRequest ? "Этот аккаунт уже существует" : "Ошибка сервера. Попробуйте позже.");
            }
        }
        catch (Exception) { await ShowErrorAsync("Сервер недоступен").ConfigureAwait(true); }
        finally { SetLoadingState(false); }
    }
    private void NavigateToMainPage()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var mainPage = Handler?.MauiContext?.Services.GetRequiredService<MainPage>();
            if (mainPage is not null && Application.Current?.Windows.Count > 0)
                Application.Current.Windows[0].Page = new NavigationPage(mainPage);
        });
    }
    private async Task ShowErrorAsync(string message)
    {
        CommonErrorLabel.Text = message;
        _ = CommonErrorLabel.FadeToAsync(1, 300);
        const uint duration = 50;
        for (int i = 0; i < 2; i++)
        {
            await FormContainer.TranslateToAsync(10, 0, duration).ConfigureAwait(true);
            await FormContainer.TranslateToAsync(-10, 0, duration).ConfigureAwait(true);
        }
        await FormContainer.TranslateToAsync(0, 0, duration).ConfigureAwait(true);
    }
    private void SetLoadingState(bool loading)
    {
        MainRegisterButton.IsEnabled = !loading;
        MainRegisterButton.Text = loading ? "Создание..." : "Создать аккаунт";
        RegisterButtonBorder.Opacity = loading ? 0.7 : 1.0;
    }
}