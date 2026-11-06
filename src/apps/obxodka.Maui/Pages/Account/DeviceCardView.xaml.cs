namespace obxodka.Pages;

//карточка активного устройства в списке профиля
public sealed partial class DeviceCardView : ContentView
{
    public event EventHandler? RemoveClicked;

    public DeviceCardView() => InitializeComponent();

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

    //обновление фона карточки под цвет темы
    public void UpdateCardOpacity(Color bgSurface) =>
        CardBorder.BackgroundColor = bgSurface;

    //клик по кнопке отзыва сессии устройства
    private void OnRemoveDeviceClicked(object? sender, TappedEventArgs e)
    {
        _ = UIAnimations.PlayIconWiggleAsync(DeleteIcon, 18);
        RemoveClicked?.Invoke(this, EventArgs.Empty);
    }
}
