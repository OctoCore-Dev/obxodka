namespace obxodka.Avalonia.Views;

public partial class DesktopSidebarView : UserControl
{
    public DesktopSidebarView() => InitializeComponent();

    private void OnNavClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tab } && DataContext is MainViewModel vm)
        {
            vm.SelectTab(tab);
        }
    }

    private void OnLogoutClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.Logout();
        }
    }
}
