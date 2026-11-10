namespace obxodka.Avalonia.Controls;

public partial class NeoDialog : UserControl
{
    public NeoDialog() => InitializeComponent();

    private void OnBackdropPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.CancelDialog();
        }
    }
}
