namespace obxodka.Views;

public partial class DeleteAccountView : ContentView
{
    private MainPage _parent = null!;
    private ApiService _apiService = null!;

    public event EventHandler? AccountDeleted;
    public event EventHandler? CancelRequested;

    public DeleteAccountView() => InitializeComponent();

    public void Initialize(MainPage parent, ApiService apiService)
    {
        _parent = parent;
        _apiService = apiService;
    }

    private void OnRootScrollViewSizeChanged(object? sender, EventArgs e)
    {
        if (RootScrollView.Height > 0)
        {
#if ANDROID || IOS
            ContentGrid.HeightRequest = RootScrollView.Height;
            RootScrollView.VerticalScrollBarVisibility = ScrollBarVisibility.Never;
            ContentGrid.Padding = RootScrollView.Height < 650 ? new Thickness(12, 16) : new Thickness(16, 20);
#else
            ContentGrid.MinimumHeightRequest = Math.Max(RootScrollView.Height, 400);
#endif
        }
    }
    private async void OnConfirmDeleteClickedAsync(object? sender, EventArgs e)
    {
        _ = DeleteAccountBorder.BounceClickAsync();
        var session = await AuthManager.LoadSessionAsync();
        if (!session.IsLoggedIn)
        {
            await ShowDeleteErrorAsync("Сначала войдите в систему.");
            return;
        }

        var confirmed = await _parent.DisplayAlertAsync("Удаление", "Удалить аккаунт навсегда? Это действие необратимо.", "Да", "Отмена");
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
                await _parent.DisplayAlertAsync("Удалено", "Аккаунт успешно удален.", "OK");
                AccountDeleted?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                await ShowDeleteErrorAsync(ApiErrorHandler.ParseGeneralError(error, "Не удалось удалить аккаунт."));
            }
        }
        catch
        {
            await ShowDeleteErrorAsync("Нет связи с сервером.");
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
        await this.ShakeErrorAsync();
    }

    private void SetDeleteLoading(bool loading)
    {
        DeleteAccountButton.IsEnabled = !loading;
        DeleteAccountButton.Text = loading ? "Удаление..." : "УДАЛИТЬ АККАУНТ";
        DeleteAccountBorder.Opacity = loading ? 0.7 : 1.0;
    }

    private void OnCancelDeleteClickedAsync(object? sender, EventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);
}
