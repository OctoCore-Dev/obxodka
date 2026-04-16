namespace obxodka.Pages;
internal partial class ChangePasswordPage : ContentPage
{
    private bool _isOldPassVisible;
    private bool _isNewPassVisible;
    private readonly ApiService _apiService;
    public ChangePasswordPage(ApiService apiService)
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
        if (this.Content == null) return;
        this.Content.Opacity = 0;
        this.Content.TranslationY = 30;
        await Task.WhenAll(
            this.Content.FadeToAsync(1, 600, Easing.CubicOut),
            this.Content.TranslateToAsync(0, 0, 600, Easing.SpringOut)
        ).ConfigureAwait(true);
    }
    private async void OnSavePasswordClicked(object? sender, EventArgs? e)
    {
        string oldP = OldPasswordEntry.Text?.Trim() ?? string.Empty;
        string newP = NewPasswordEntry.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(oldP) || string.IsNullOrWhiteSpace(newP))
        {
            await DisplayAlertAsync("Внимание", "Заполните все поля", "OK");
            return;
        }
        if (newP.Length < 6)
        {
            await DisplayAlertAsync("Ошибка", "Новый пароль должен быть не менее 6 символов", "OK");
            return;
        }
        var button = sender as Microsoft.Maui.Controls.Button;
        if (button != null) _ = button.ScaleToAsync(0.95, 100).ContinueWith(t => button.ScaleToAsync(1.0, 100));
        button.IsEnabled = false;
        try
        {
            var session = await AuthManager.LoadSessionAsync().ConfigureAwait(true);
            var (success, error) = await _apiService.ChangePasswordAsync(session.Email ?? "", oldP, newP).ConfigureAwait(true);
            if (success)
            {
                await DisplayAlertAsync("Успех", "Пароль обновлен!", "OK");
                session.Password = newP;
                await AuthManager.SaveSessionAsync(session).ConfigureAwait(true);
                await Navigation.PopAsync().ConfigureAwait(true);
            }
            else
            {
                await DisplayAlertAsync("Ошибка", error ?? "Не удалось изменить пароль", "OK");
            }
        }
        catch (Exception)
        {
            await DisplayAlertAsync("Ошибка", "Нет связи с сервером", "OK");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }
    private void OnOldPasswordEyeClicked(object? sender, EventArgs? e)
    {
        _isOldPassVisible = !_isOldPassVisible;
        OldPasswordEntry.IsPassword = !_isOldPassVisible;
        OldEyeImg.Source = _isOldPassVisible ? "eye_icon.png" : "eye_off.png";
    }
    private void OnNewPasswordEyeClicked(object? sender, EventArgs? e)
    {
        _isNewPassVisible = !_isNewPassVisible;
        NewPasswordEntry.IsPassword = !_isNewPassVisible;
        NewEyeImg.Source = _isNewPassVisible ? "eye_icon.png" : "eye_off.png";
    }
}