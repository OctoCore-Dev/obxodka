namespace obxodka.UITests;

public class AppiumSetupTests : IDisposable
{
    private WindowsDriver? _session;

    private const string WindowsApplicationDriverUrl = "http://127.0.0.1:4723";
    private const string AppId = "C:\\Users\\irovb\\Documents\\code\\obxodka\\obxodka\\bin\\Debug\\net10.0-windows10.0.19041.0\\win-x64\\obxodka.exe";

    public AppiumSetupTests()
    {
        var appiumOptions = new AppiumOptions
        {
            App = AppId,
            DeviceName = "WindowsPC"
        };
        _session = new WindowsDriver(new Uri(WindowsApplicationDriverUrl), appiumOptions);
        _session.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(5);
    }

    [Fact]
    public void AppLaunchesSuccessfully()
    {
        Assert.NotNull(_session);
        var title = _session.Title;
        Assert.Contains("obxodka", title, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _session?.Quit();
        _session?.Dispose();
        _session = null;
        GC.SuppressFinalize(this);
    }
}
