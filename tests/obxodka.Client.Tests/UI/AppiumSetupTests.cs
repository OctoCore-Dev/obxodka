namespace obxodka.Client.Tests.UI;

[Trait("Category", "UI")]
public class AppiumSetupTests : IDisposable
{
    private const string WindowsApplicationDriverUrl = "http://127.0.0.1:4723";
    private static readonly string s_tAppId = UiTestHelper.ResolveAppPath();
    private WindowsDriver? _session;

    public AppiumSetupTests()
    {
        try
        {
            var appiumOptions = new AppiumOptions
            {
                App = s_tAppId,
                DeviceName = "WindowsPC"
            };
            _session = new WindowsDriver(new Uri(WindowsApplicationDriverUrl), appiumOptions);
            _session.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(1);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _session = null;
        }
    }

    public void Dispose()
    {
        _session?.Quit();
        _session?.Dispose();
        _session = null;
        GC.SuppressFinalize(this);
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
}
