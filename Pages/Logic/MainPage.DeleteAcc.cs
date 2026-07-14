namespace obxodka.Pages;

internal sealed partial class MainPage
{
    private async void OnConfirmDeleteClickedAsync(object? sender, EventArgs e)
    {
        _ = DeleteAccountBorder.BounceClickAsync();
        var session = await AuthManager.LoadSessionAsync();
        if (!session.IsLoggedIn)
        {
            await ShowDeleteErrorAsync("Сначала нужно войти.");
            return;
        }

        var confirmed = await DisplayAlertAsync("Внимание", "Удалить аккаунт навсегда? Это действие необратимо.", "Да", "Нет");
        if (!confirmed)
        {
            return;
        }

        SetDeleteLoading(true);
        try
        {
            var (success, error) = await _apiService.DeleteAccountAsync();
            if (success)
            {
                await AuthManager.ClearSessionAsync();
                await DisplayAlertAsync("Готово", "Аккаунт и все данные стерты", "OK");

                DesktopSidebar.IsVisible = false;
                MobileBottomBar.IsVisible = false;
                SwitchTab("auth");
                _ = UIAnimations.PlayEntranceCascadeAsync(100, 600, AppIconLeft, FormContainer);
                _ = UIAnimations.PlayEntranceCascadeAsync(100, 600, AppIconLogin, TitleLabelLogin, LoginButtonBorder, SwitchToRegisterLabel);
            }
            else
            {
                await ShowDeleteErrorAsync(ApiErrorHandler.ParseGeneralError(error, "Не удалось удалить аккаунт."));
            }
        }
        catch
        {
            await ShowDeleteErrorAsync("Проблема с сетью.");
        }
        finally
        {
            SetDeleteLoading(false);
        }
    }

    private async Task ShowDeleteErrorAsync(string msg)
    {
        DeleteErrorLabel.Text = msg;
        _ = DeleteErrorLabel.FadeToAsync(1, 200);
        await TabContentDelete.ShakeErrorAsync();
    }

    private void SetDeleteLoading(bool loading)
    {
        DeleteAccountButton.IsEnabled = !loading;
        DeleteAccountButton.Text = loading ? "УДАЛЕНИЕ..." : "Стереть данные";
        DeleteAccountBorder.Opacity = loading ? 0.7 : 1.0;
    }

    private void OnNavigateToDeleteTabClicked(object? sender, EventArgs e) => SwitchTab("delete");
    private void OnCancelDeleteClicked(object? sender, EventArgs e) => SwitchTab("profile");
}
