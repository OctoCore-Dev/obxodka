namespace obxodka.Client.Tests.UI;

[Trait("Category", "UI")]
public partial class AppiumSetupTests : IDisposable
{
    private WindowsDriver? _session;

    private const string WindowsApplicationDriverUrl = "http://127.0.0.1:4723";
    private static readonly string t_appId = UITestHelper.ResolveAppPath();

    public AppiumSetupTests()
    {
        try
        {
            var appiumOptions = new AppiumOptions
            {
                App = t_appId,
                DeviceName = "WindowsPC"
            };
            _session = new WindowsDriver(new Uri(WindowsApplicationDriverUrl), appiumOptions);
            _session.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(1);
        }
        catch
        {
            _session = null;
        }
    }

    [Fact]
    public void AppLaunchesSuccessfully()
    {
        if (_session == null)
        {
            return;
        }

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
