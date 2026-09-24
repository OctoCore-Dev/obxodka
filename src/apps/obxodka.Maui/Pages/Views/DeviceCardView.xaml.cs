namespace obxodka.Views;

public sealed partial class DeviceCardView : ContentView
{
    public event EventHandler? RemoveClicked;

    public DeviceCardView() => InitializeComponent();

    public void SetIsCurrentDevice(bool isCurrent)
    {
        DeleteBtn.IsVisible = !isCurrent;
        CurrentDeviceBadge.IsVisible = isCurrent;

        CardBorder.ClearValue(Border.StrokeProperty);
        CardBorder.SetDynamicResource(Border.StrokeProperty, isCurrent ? "Primary" : "BorderSubtle");
        CardBorder.StrokeThickness = isCurrent ? 1.5 : 1.0;
    }

    public void UpdateCardOpacity(Color bgSurface) =>
        CardBorder.BackgroundColor = bgSurface;

    private void OnRemoveDeviceClicked(object? sender, TappedEventArgs e)
    {
        _ = UIAnimations.PlayIconWiggleAsync(DeleteIcon, 18);
        RemoveClicked?.Invoke(this, EventArgs.Empty);
    }
}
