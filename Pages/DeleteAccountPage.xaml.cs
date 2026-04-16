namespace obxodka.Pages;
internal partial class DeleteAccountPage : ContentPage
{
    private readonly ApiService _apiService;
    public DeleteAccountPage(ApiService apiService)
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
        this.Content.Scale = 0.9;
        this.Content.TranslationY = 30;
        await Task.WhenAll(
            this.Content.FadeToAsync(1, 600, Easing.CubicOut),
            this.Content.ScaleToAsync(1, 600, Easing.SpringOut),
            this.Content.TranslateToAsync(0, 0, 600, Easing.SpringOut)
        ).ConfigureAwait(true);
    }
    private async void OnConfirmDeleteClicked(object? sender, EventArgs? e)
    {
        var button = sender as Microsoft.Maui.Controls.Button;
        if (button != null) _ = button.ScaleToAsync(0.95, 100).ContinueWith(t => button.ScaleToAsync(1.0, 100));
        var session = await AuthManager.LoadSessionAsync().ConfigureAwait(true);
        if (!session.IsLoggedIn)
        {
            await DisplayAlertAsync("Ошибка", "Сначала нужно войти в аккаунт", "OK");
            return;
        }
        bool isConfirmed = await DisplayAlertAsync("Удаление", "Удалить навсегда?", "Да", "Нет");
        if (!isConfirmed) return;
        var (success, error) = await _apiService.DeleteAccountAsync(session.Email ?? "");
        if (success)
        {
            AuthManager.ClearSession();
            await DisplayAlertAsync("Успех", "Аккаунт удален", "OK");
            NavigateToLoginPage();
        }
        else await DisplayAlertAsync("Ошибка", "Недостаточно прав или сессия истекла. Перезайдите в аккаунт.", "OK");
    }
    private void NavigateToLoginPage()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var loginPage = Handler?.MauiContext?.Services.GetRequiredService<LoginPage>();
            if (loginPage is not null && Application.Current?.Windows.Count > 0)
                Application.Current.Windows[0].Page = new NavigationPage(loginPage);
        });
    }
}