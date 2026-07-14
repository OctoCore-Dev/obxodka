using obxodka.Pages;
namespace obxodka;

public sealed partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        BuildNavigation();
    }
    private void BuildNavigation()
    {
        FlyoutBehavior = FlyoutBehavior.Disabled;
        Items.Add(new ShellContent { Route = "main", FlyoutItemIsVisible = false, ContentTemplate = new DataTemplate(typeof(MainPage)) });
    }
}
