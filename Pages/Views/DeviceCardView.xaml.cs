namespace obxodka.Views;

public sealed partial class DeviceCardView : ContentView
{
    public event EventHandler? RemoveClicked;

    public DeviceCardView() => InitializeComponent();

    private async void OnRemoveDeviceClickedAsync(object? sender, TappedEventArgs e)
    {
        _ = UIAnimations.PlayIconWiggleAsync(DeleteIcon, 18);
        _ = await DeleteBtn.ScaleToAsync(0.9, 80, Easing.CubicOut);
        _ = await DeleteBtn.ScaleToAsync(1.0, 100, Easing.SpringOut);
        RemoveClicked?.Invoke(this, EventArgs.Empty);
    }
}
