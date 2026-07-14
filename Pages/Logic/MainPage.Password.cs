namespace obxodka.Pages;

internal sealed partial class MainPage
{
    private void OnOldPasswordEyeClicked(object? sender, EventArgs e)
    {
        _isOldPassVisible = !_isOldPassVisible;
        OldPasswordEntry.IsPassword = !_isOldPassVisible;
        OldEyeImg.Icon = _isOldPassVisible ? FluentIcons.EyeTracking24 : FluentIcons.EyeOff24;
        OldEyeImg.IconColor = GetThemeColor(_isOldPassVisible ? "Tertiary" : "Gray500");
    }

    private void OnNewPasswordEyeClicked(object? sender, EventArgs e)
    {
        _isNewPassVisible = !_isNewPassVisible;
        NewPasswordEntry.IsPassword = !_isNewPassVisible;
        NewEyeImg.Icon = _isNewPassVisible ? FluentIcons.EyeTracking24 : FluentIcons.EyeOff24;
        NewEyeImg.IconColor = GetThemeColor(_isNewPassVisible ? "Tertiary" : "Gray500");
    }

    private async void OnSavePasswordClickedAsync(object? sender, EventArgs e)
    {
        _ = SavePasswordBorder.BounceClickAsync();
        var oldP = OldPasswordEntry.Text?.Trim() ?? string.Empty;
        var newP = NewPasswordEntry.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(oldP) || string.IsNullOrWhiteSpace(newP))
        {
            await ShowPasswordErrorAsync("Заполните все поля");
            return;
        }
        if (newP.Length < 6)
        {
            await ShowPasswordErrorAsync("Новый пароль должен быть не менее 6 символов");
            return;
        }
        SetPasswordLoading(true);
        _ = PasswordErrorLabel.FadeToAsync(0, 100);
        try
        {
            var session = await AuthManager.LoadSessionAsync();
            var (success, error) = await _apiService.ChangePasswordAsync(session.Email ?? "", oldP, newP);
            if (success)
            {
                session.Password = newP;
                await AuthManager.SaveSessionAsync(session);
                OldPasswordEntry.Text = "";
                NewPasswordEntry.Text = "";
                await DisplayAlertAsync("Успех", "Пароль изменён", "OK");
            }
            else
            {
                await ShowPasswordErrorAsync(ApiErrorHandler.ParseGeneralError(error, "Не удалось изменить пароль"));
            }
        }
        catch
        {
            await ShowPasswordErrorAsync("Нет связи с сервером");
        }
        finally
        {
            SetPasswordLoading(false);
        }
    }

    private async Task ShowPasswordErrorAsync(string msg)
    {
        PasswordErrorLabel.Text = msg;
        _ = PasswordErrorLabel.FadeToAsync(1, 200);
        await TabContentPassword.ShakeErrorAsync();
    }

    private void SetPasswordLoading(bool loading)
    {
        SavePasswordButton.IsEnabled = !loading;
        SavePasswordButton.Text = loading ? "Сохранение..." : "Сохранить";
        SavePasswordBorder.Opacity = loading ? 0.7 : 1.0;
    }
}
