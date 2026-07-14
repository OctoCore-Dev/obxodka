namespace obxodka.Views;

public partial class DeviceCardView : ContentView
{
    public event EventHandler? RemoveClicked;

    public DeviceCardView() => InitializeComponent();

    private void OnRemoveDeviceClickedAsync(object sender, EventArgs e) => RemoveClicked?.Invoke(this, EventArgs.Empty);
}
