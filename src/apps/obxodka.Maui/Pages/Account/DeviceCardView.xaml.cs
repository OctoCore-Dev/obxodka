namespace obxodka.Pages.Account;

//карточка активного устройства в списке профиля
public sealed partial class DeviceCardView
{
    public DeviceCardView() => InitializeComponent();
    public event EventHandler? RemoveClicked;

    //обновление фона карточки под цвет темы
    public void UpdateCardOpacity(Color bgSurface) =>
        CardBorder.BackgroundColor = bgSurface;

    //подсветка текущего устройства и скрытие кнопки удаления
    public void SetIsCurrentDevice(bool isCurrent)
    {
        DeleteBtn.IsVisible = !isCurrent;
        CurrentDeviceBadge.IsVisible = isCurrent;

        if (BindingContext is DeviceItem device)
        {
            LastActiveLabel.Text = isCurrent
                ? AccountMessages.ActiveNow
                : $"Активен: {device.LastActive.ToLocalTime():dd.MM.yyyy HH:mm}";
        }

        CardBorder.ClearValue(Border.StrokeProperty);
        CardBorder.SetDynamicResource(Border.StrokeProperty, isCurrent ? "Primary" : "BorderSubtle");
        CardBorder.StrokeThickness = isCurrent ? 1.5 : 1.0;
    }

    //клик по кнопке отзыва сессии устройства
    private void OnRemoveDeviceClicked(object? sender, TappedEventArgs e)
    {
        _ = DeleteIcon.PlayIconWiggleAsync(18);
        RemoveClicked?.Invoke(this, EventArgs.Empty);
    }
}
