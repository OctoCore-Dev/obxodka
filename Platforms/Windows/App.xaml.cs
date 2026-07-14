using Microsoft.UI.Xaml;
namespace obxodka.WinUI;

public sealed partial class App : MauiWinUIApplication
{
    public App()
    {
        InitializeComponent();
        _ = WinUIEx.WebAuthenticator.CheckOAuthRedirectionActivation();
        UnhandledException += App_UnhandledException;
    }
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    protected override void OnLaunched(LaunchActivatedEventArgs args) => base.OnLaunched(args);
    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Obxodka", "unhandled.log");
            _ = Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var logEntry = $"[{DateTime.UtcNow:O}] {e.Exception}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(logPath, logEntry);
            e.Handled = true;
        }
        catch { }
    }
}
