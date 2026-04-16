namespace obxodka.WinUI;
public partial class App : MauiWinUIApplication
{
    public App()
    {
    }
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);
    }
}