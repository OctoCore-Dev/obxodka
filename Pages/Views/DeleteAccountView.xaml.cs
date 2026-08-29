namespace obxodka.Views;

public sealed partial class DeleteAccountView : ContentView
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

    public Task PlayEntranceAnimationAsync() =>
        UIAnimations.PlayEntranceFadeScaleAsync(DeleteCardContainer);

    private async void OnConfirmDeleteClickedAsync(object? sender, EventArgs e)
    {
        _ = DeleteAccountBorder.BounceClickAsync();
        _ = UIAnimations.HideErrorLabelAsync(DeleteErrorLabel);

        var session = await AuthManager.LoadSessionAsync();
        if (!session.IsLoggedIn)
        {
            await ShowDeleteErrorAsync("Сначала войдите в систему.");
            return;
        }

        var confirmed = await _parent.DisplayAlertAsync("Удаление", "Удалить аккаунт навсегда? Это действие необратимо.", "Да, удалить", "Отмена");
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
        catch (Exception ex)
        {
            Debug.WriteLine($"[DELETE ACCOUNT ERROR] {ex.Message}");
            await ShowDeleteErrorAsync(ApiErrorHandler.ParseGeneralError(ex.Message, "Нет связи с сервером."));
        }
        finally
        {
            SetDeleteLoading(false);
        }
    }

    private async Task ShowDeleteErrorAsync(string msg)
    {
        DeleteErrorLabel.Text = msg;
        await UIAnimations.ShowErrorLabelAsync(DeleteErrorLabel);
        await DeleteCardContainer.ShakeErrorAsync();
    }

    private void SetDeleteLoading(bool loading)
    {
        DeleteAccountButton.IsEnabled = !loading;
        DeleteAccountButton.Text = loading ? "УДАЛЕНИЕ..." : "УДАЛИТЬ АККАУНТ";
        DeleteAccountBorder.Opacity = loading ? 0.6 : 1.0;
    }

    private void OnCancelDeleteClicked(object? sender, EventArgs e) =>
        CancelRequested?.Invoke(this, EventArgs.Empty);
}
