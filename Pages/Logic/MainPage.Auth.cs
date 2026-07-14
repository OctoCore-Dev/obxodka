namespace obxodka.Pages;

internal sealed partial class MainPage
{
    private bool _isLoginPasswordVisible;
    private bool _isRegPasswordVisible;
    private bool _isRegRepeatPasswordVisible;
    private int _lockSeconds;
    private bool _isTimerRunning;
    private IDispatcherTimer? _lockTimer;

    private void OnSwitchToRegisterTapped(object? sender, TappedEventArgs? e)
    {
        _ = Task.WhenAll(
            LoginContainer.FadeToAsync(0, 200),
            LoginContainer.ScaleToAsync(0.95, 200)
        ).ContinueWith(t => MainThread.BeginInvokeOnMainThread(() => LoginContainer.IsVisible = false));

        RegisterContainer.IsVisible = true;
        RegisterContainer.Scale = 0.95;
        _ = Task.WhenAll(
            RegisterContainer.FadeToAsync(1, 200),
            RegisterContainer.ScaleToAsync(1, 200)
        );
    }

    private void OnSwitchToLoginTapped(object? sender, TappedEventArgs? e)
    {
        _ = Task.WhenAll(
            RegisterContainer.FadeToAsync(0, 200),
            RegisterContainer.ScaleToAsync(0.95, 200)
        ).ContinueWith(t => MainThread.BeginInvokeOnMainThread(() => RegisterContainer.IsVisible = false));

        LoginContainer.IsVisible = true;
        LoginContainer.Scale = 0.95;
        _ = Task.WhenAll(
            LoginContainer.FadeToAsync(1, 200),
            LoginContainer.ScaleToAsync(1, 200)
        );
    }

    private void OnLoginPasswordEyeClicked(object? sender, EventArgs? e)
    {
        _isLoginPasswordVisible = !_isLoginPasswordVisible;
        LoginPasswordEntry.IsPassword = !_isLoginPasswordVisible;
        LoginEyeImg.Icon = _isLoginPasswordVisible ? FluentIcons.EyeTracking24 : FluentIcons.EyeOff24;
        LoginEyeImg.IconColor = GetThemeColor(_isLoginPasswordVisible ? "Primary" : "Gray500");
    }

    private async void OnLoginClickedAsync(object? sender, EventArgs? e)
    {
        _ = LoginButtonBorder.BounceClickAsync();
        var email = LoginEmailEntry.Text?.Trim() ?? string.Empty;
        var password = LoginPasswordEntry.Text;
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            await ShowLoginErrorAsync("Заполните все поля");
            return;
        }
        SetLoginLoadingState(true);
        _ = LoginErrorLabel.FadeToAsync(0, 100);
        try
        {
            var (success, data, error) = await _apiService.LoginAsync(new AuthRequest(
                email, password, DeviceHelper.GetHwid(), DeviceHelper.GetDeviceName()
            ));
            if (success && data is not null)
            {
                await AuthManager.SaveSessionAsync(new UserSession
                {
                    Email = email,
                    Password = password,
                    JwtToken = data.Token,
                    VpnConfig = data.VpnConfig,
                    SubscriptionUntil = data.SubscriptionUntil,
                    IsLoggedIn = true
                });
                await SwitchToAppAfterAuthAsync();
            }
            else
            {
                if (error is not null && error.Contains("retryAfterSeconds", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var json = JsonDocument.Parse(error);
                        if (json.RootElement.TryGetProperty("retryAfterSeconds", out var rAs))
                        {
                            StartLockTimer(rAs.GetInt32());
                            return;
                        }
                    }
                    catch { }
                }
                await ShowLoginErrorAsync(ApiErrorHandler.ParseLoginError(error));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LOGIN ERROR] {ex.Message}");
            await ShowLoginErrorAsync("Нет связи с сервером");
        }
        finally
        {
            if (!_isTimerRunning)
            {
                SetLoginLoadingState(false);
            }
        }
    }

    private async Task ShowLoginErrorAsync(string msg)
    {
        LoginErrorLabel.Text = msg;
        _ = LoginErrorLabel.FadeToAsync(1, 200);
        await FormContainer.ShakeErrorAsync();
    }

    private void SetLoginLoadingState(bool isLoading, string? overrideText = null)
    {
        MainLoginButton.IsEnabled = !isLoading;
        MainLoginButton.Text = overrideText ?? (isLoading ? "Вход..." : "Войти");
        LoginButtonBorder.Opacity = isLoading ? 0.7 : 1.0;
    }

    private void StartLockTimer(int seconds)
    {
        _lockSeconds = seconds;

        if (_isTimerRunning)
        {
            return;
        }

        _isTimerRunning = true;
        SetLoginLoadingState(true, "Заблокировано");
        _lockTimer = Application.Current?.Dispatcher.CreateTimer();

        if (_lockTimer is null)
        {
            return;
        }

        _lockTimer.Interval = TimeSpan.FromSeconds(1);
        _lockTimer.Tick += (s, e) =>
        {
            _lockSeconds--;
            if (_lockSeconds <= 0)
            {
                _isTimerRunning = false;
                _lockTimer.Stop();
                SetLoginLoadingState(false);
                LoginErrorLabel.Text = "";
                LoginErrorLabel.Opacity = 0;
            }
            else
            {
                var ts = TimeSpan.FromSeconds(_lockSeconds);
                LoginErrorLabel.Text = $"Слишком много попыток.\nЖдите: {ts.Minutes:D2}:{ts.Seconds:D2}";
                LoginErrorLabel.Opacity = 1;
            }
        };
        _lockTimer.Start();
    }

    private void OnRegPasswordEyeClicked(object? sender, EventArgs? e)
    {
        _isRegPasswordVisible = !_isRegPasswordVisible;
        RegPasswordEntry.IsPassword = !_isRegPasswordVisible;
        RegEyeImg1.Icon = _isRegPasswordVisible ? FluentIcons.EyeTracking24 : FluentIcons.EyeOff24;
        RegEyeImg1.IconColor = GetThemeColor(_isRegPasswordVisible ? "Primary" : "Gray500");
    }

    private void OnRegRepeatPasswordEyeClicked(object? sender, EventArgs? e)
    {
        _isRegRepeatPasswordVisible = !_isRegRepeatPasswordVisible;
        RegPasswordRepeatEntry.IsPassword = !_isRegRepeatPasswordVisible;
        RegEyeImg2.Icon = _isRegRepeatPasswordVisible ? FluentIcons.EyeTracking24 : FluentIcons.EyeOff24;
        RegEyeImg2.IconColor = GetThemeColor(_isRegRepeatPasswordVisible ? "Primary" : "Gray500");
    }

    private async void OnPolicyLinkClickAsync(object? sender, TappedEventArgs? e)
    {
        try
        {
            _ = await Launcher.Default.OpenAsync("https://obxodka.one/Home/Privacy");
        }
        catch { }
    }

    private async void OnRegisterClickedAsync(object? sender, EventArgs? e)
    {
        _ = RegisterButtonBorder.BounceClickAsync();
        if (!PolicyCheckBox.IsChecked)
        {
            await ShowRegErrorAsync("Примите условия");
            return;
        }
        var email = RegEmailEntry.Text?.Trim() ?? string.Empty;
        var pass = RegPasswordEntry.Text;
        var confirm = RegPasswordRepeatEntry.Text;
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(pass) || pass.Length < 6 || confirm is null || pass != confirm)
        {
            await ShowRegErrorAsync("Проверьте правильность полей");
            return;
        }
        SetRegLoadingState(true);
        _ = RegErrorLabel.FadeToAsync(0, 100);
        try
        {
            var authRequest = new AuthRequest(email, pass, DeviceHelper.GetHwid(), DeviceInfo.Current.Name);
            var (regSuccess, regError) = await _apiService.RegisterAsync(authRequest);
            if (regSuccess)
            {
                var (loginSuccess, loginData, _) = await _apiService.LoginAsync(authRequest);
                if (loginSuccess && loginData is not null)
                {
                    await AuthManager.SaveSessionAsync(new UserSession
                    {
                        Email = email,
                        Password = pass,
                        JwtToken = loginData.Token,
                        VpnConfig = loginData.VpnConfig,
                        SubscriptionUntil = loginData.SubscriptionUntil,
                        IsLoggedIn = true
                    });
                    await SwitchToAppAfterAuthAsync();
                }
                else
                {
                    await ShowRegErrorAsync("Аккаунт создан, но войти не удалось.");
                }
            }
            else
            {
                await ShowRegErrorAsync(ApiErrorHandler.ParseRegistrationError(regError));
            }
        }
        catch (Exception)
        {
            await ShowRegErrorAsync("Нет связи с сервером.");
        }
        finally
        {
            SetRegLoadingState(false);
        }
    }

    private async Task ShowRegErrorAsync(string message)
    {
        RegErrorLabel.Text = message;
        _ = RegErrorLabel.FadeToAsync(1, 300);
        await FormContainer.ShakeErrorAsync();
    }

    private void SetRegLoadingState(bool loading)
    {
        MainRegisterButton.IsEnabled = !loading;
        MainRegisterButton.Text = loading ? "Ждите..." : "Создать аккаунт";
        RegisterButtonBorder.Opacity = loading ? 0.7 : 1.0;
    }

    private async Task SwitchToAppAfterAuthAsync()
    {
        if (DeviceInfo.Current.Idiom == DeviceIdiom.Desktop)
        {
            DesktopSidebar.IsVisible = true;
            _ = DesktopSidebar.FadeToAsync(1, 400);
        }
        else
        {
            MobileBottomBar.IsVisible = true;
            _ = MobileBottomBar.FadeToAsync(1, 400);
        }
        SwitchTab("vpn");

        var session = await AuthManager.LoadSessionAsync();
        RemainingSeconds = session.SubscriptionUntil.HasValue 
            ? Math.Max(0, (long)(session.SubscriptionUntil.Value - DateTime.UtcNow).TotalSeconds)
            : 0;
        UpdateBalanceUI();
        LoadProfileTabDataAsync(session);
    }
}
