namespace obxodka.Pages;

//экран подтверждения безвозвратного удаления аккаунта
public sealed partial class DeleteAccountView : ContentView
{
    private MainPage _parent = null!;
    private ApiService _apiService = null!;

    public event EventHandler? AccountDeleted;
    public event EventHandler? CancelRequested;

    public DeleteAccountView() => InitializeComponent();

    //инициализация зависимостей экрана удаления
    public void Initialize(MainPage parent, ApiService apiService)
    {
        _parent = parent;
        _apiService = apiService;
    }

    //анимация появления карточки
    public Task PlayEntranceAnimationAsync() =>
        UIAnimations.PlayEntranceFadeScaleAsync(DeleteCardContainer);

    //актуализация цвета фона карточки под активную тему
    public void UpdateCardOpacity()
    {
        var bgSurface = (Application.Current?.Resources.TryGetValue("BgSurface", out var bg) == true && bg is Color bgColor)
            ? bgColor
            : Color.FromArgb("#161622");

        DeleteCardContainer.BackgroundColor = bgSurface;
    }

    //отступ под системный статусбар мобильных устройств
    public void SetHeaderTopInset(double top)
    {
        if (DeviceInfo.Idiom == DeviceIdiom.Phone && RootDeleteGrid != null)
        {
            RootDeleteGrid.Padding = new Thickness(20, Math.Max(top + 16, 16), 20, 16);
        }
    }

    //обновление оформления при смене темы
    public void OnThemeChanged() =>
        MainThread.BeginInvokeOnMainThread(UpdateCardOpacity);

    //удаление профиля на бэкенде и очистка локальной сессии
    private async void OnConfirmDeleteClickedAsync(object? sender, EventArgs e)
    {
        _ = DeleteAccountButton.BounceClickAsync();
        _ = UIAnimations.HideErrorLabelAsync(DeleteErrorLabel);

        var session = await AuthManager.LoadSessionAsync();
        if (!session.IsLoggedIn)
        {
            await ShowDeleteErrorAsync(AccountMessages.LoginRequired);
            return;
        }

        var confirmed = await _parent.DisplayAlertAsync(AccountMessages.DeleteDeviceTitle, AccountMessages.DeleteAccountConfirm, AccountMessages.ButtonConfirmDelete, AccountMessages.ButtonCancel);
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
                await _parent.DisplayAlertAsync(AccountMessages.DeleteDeviceTitle, AccountMessages.DeleteAccountSuccess, AccountMessages.ButtonOk);
                AccountDeleted?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                await ShowDeleteErrorAsync(ApiErrorHandler.ParseGeneralError(error, AccountMessages.DeleteAccountFailed));
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[DELETE ACCOUNT ERROR] {ex.Message}", ex);
            await ShowDeleteErrorAsync(ApiErrorHandler.ParseGeneralError(ex.Message, AccountMessages.NoServerConnection));
        }
        finally
        {
            SetDeleteLoading(false);
        }
    }

    //отображение текста ошибки с анимацией тряски
    private async Task ShowDeleteErrorAsync(string msg)
    {
        DeleteErrorLabel.Text = msg;
        await UIAnimations.ShowErrorLabelAsync(DeleteErrorLabel);
        await DeleteCardContainer.ShakeErrorAsync();
    }

    //блокировка кнопки и показ индикатора удаления
    private void SetDeleteLoading(bool loading)
    {
        DeleteAccountButton.IsEnabled = !loading;
        DeleteAccountButton.Text = loading ? AccountMessages.DeletingProgress : AccountMessages.DeleteAccountButton;
    }

    //отмена удаления и закрытие карточки
    private void OnCancelDeleteClicked(object? sender, EventArgs e) =>
        CancelRequested?.Invoke(this, EventArgs.Empty);
}
