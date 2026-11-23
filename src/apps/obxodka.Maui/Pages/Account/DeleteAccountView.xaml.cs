namespace obxodka.Pages.Account;

//экран подтверждения безвозвратного удаления аккаунта
public sealed partial class DeleteAccountView : IAdaptivePage
{
    private ApiService _apiService = null!;
    private MainPage _parent = null!;

    public DeleteAccountView() => InitializeComponent();

    public event EventHandler? AccountDeleted;
    public event EventHandler? CancelRequested;

    //анимация появления карточки
    public Task PlayEntranceAnimationAsync() =>
        UiAnimations.PlayEntranceFadeScaleAsync(DeleteCardContainer);

    //обновление оформления при смене темы
    public void OnThemeChanged() =>
        MainThread.BeginInvokeOnMainThread(UpdateCardOpacity);

    //отмена удаления и закрытие карточки
    private void OnCancelDeleteClicked(object? sender, EventArgs e) =>
        CancelRequested?.Invoke(this, EventArgs.Empty);

    //инициализация зависимостей экрана удаления
    public void Initialize(MainPage parent, ApiService apiService)
    {
        _parent = parent;
        _apiService = apiService;
    }

    //актуализация цвета фона карточки под активную тему
    public void UpdateCardOpacity()
    {
        var bgSurface = Application.Current?.Resources.TryGetValue("BgSurface", out var bg) == true && bg is Color bgColor
            ? bgColor
            : Color.FromArgb("#161622");

        DeleteCardContainer.BackgroundColor = bgSurface;
    }

    //единая адаптация экрана удаления аккаунта по ssot-снимку геометрии окна
    public void ApplyViewport(AdaptiveViewport viewport)
    {
        if (RootDeleteGrid is not null)
        {
            var topPad = DeviceInfo.Platform == DevicePlatform.Android || DeviceInfo.Platform == DevicePlatform.iOS
                ? Math.Max(viewport.SafeTop + 16.0, 24.0)
                : 24.0;
            var bottomPad = !viewport.UseSidebar
                ? Math.Max(104.0, viewport.SafeBottom + 92.0)
                : Math.Max(24.0, viewport.SafeBottom + 16.0);
            RootDeleteGrid.Padding = new Thickness(20, topPad, 20, bottomPad);
        }

        if (DeleteCardContainer is not null && viewport.ContentWidth > 0)
        {
            var targetWidth = Math.Clamp(viewport.ContentWidth - 40.0, 260.0, 480.0);
            if (Math.Abs(DeleteCardContainer.WidthRequest - targetWidth) > 0.5)
            {
                DeleteCardContainer.WidthRequest = targetWidth;
            }
        }
    }

    //удаление профиля на бэкенде и очистка локальной сессии
    private async void OnConfirmDeleteClickedAsync(object? sender, EventArgs e)
    {
        try
        {
            _ = DeleteAccountButton.BounceClickAsync();
            _ = UiAnimations.HideErrorLabelAsync(DeleteErrorLabel);

            var session = await AuthManager.LoadSessionAsync();
            if (!session.IsLoggedIn)
            {
                await ShowDeleteErrorAsync(AccountMessages.LoginRequired);
                return;
            }

            var confirmed = await _parent.DisplayAlertAsync(AccountMessages.DeleteDeviceTitle, AccountMessages.DeleteAccountConfirm,
                AccountMessages.ButtonConfirmDelete, AccountMessages.ButtonCancel);
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
                    await _parent.DisplayAlertAsync(AccountMessages.DeleteDeviceTitle, AccountMessages.DeleteAccountSuccess,
                        AccountMessages.ButtonOk);
                    AccountDeleted?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    await ShowDeleteErrorAsync(ApiErrorHandler.ParseGeneralError(error, AccountMessages.DeleteAccountFailed));
                }
            }
            finally
            {
                SetDeleteLoading(false);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[DELETE ACCOUNT ERROR] {ex.Message}", ex);
            await ShowDeleteErrorAsync(ApiErrorHandler.ParseGeneralError(ex.Message, AccountMessages.NoServerConnection));
        }
    }

    //отображение текста ошибки с анимацией тряски
    private async Task ShowDeleteErrorAsync(string msg)
    {
        DeleteErrorLabel.Text = msg;
        await UiAnimations.ShowErrorLabelAsync(DeleteErrorLabel);
        await DeleteCardContainer.ShakeErrorAsync();
    }

    //блокировка кнопки и показ индикатора удаления
    private void SetDeleteLoading(bool loading)
    {
        DeleteAccountButton.IsEnabled = !loading;
        DeleteAccountButton.Text = loading ? AccountMessages.DeletingProgress : AccountMessages.DeleteAccountButton;
    }
}
