namespace obxodka.UITests;

public class VpnUITests : IDisposable
{
    private WindowsDriver? _session;

    private const string WindowsApplicationDriverUrl = "http://127.0.0.1:4723";
    private const string AppId = "C:\\BuildCache\\obxodka\\bin\\debug_net10.0-windows10.0.19041.0_win-x64\\obxodka.exe";

    public VpnUITests()
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
    public void ConnectButtonClickChangesStatus()
    {
        if (_session == null)
        {
            return;
        }

        var connectButton = _session.FindElement(MobileBy.AccessibilityId("VpnConnectButton"));
        var statusLabel = _session.FindElement(MobileBy.AccessibilityId("VpnStatusLabel"));

        Assert.Equal("Не в сети", statusLabel.Text);

        connectButton.Click();

        Assert.NotEqual("Не в сети", statusLabel.Text);
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        GC.SuppressFinalize(this);
    }
}
