namespace obxodka.UITests;

public class AuthUITests : IDisposable
{
    private WindowsDriver? _session;

    private const string WindowsApplicationDriverUrl = "http://127.0.0.1:4723";
    private const string AppId = "C:\\BuildCache\\obxodka\\bin\\debug_net10.0-windows10.0.19041.0_win-x64\\obxodka.exe";

    public AuthUITests()
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
    public void InvalidEmailShowsErrorLabel()
    {
        if (_session == null)
        {
            return;
        }

        var emailInput = _session.FindElement(MobileBy.AccessibilityId("AuthEmailInput"));
        emailInput.SendKeys("invalid_email");

        var getCodeButton = _session.FindElement(MobileBy.AccessibilityId("AuthGetCodeButton"));
        getCodeButton.Click();

        var errorLabel = _session.FindElement(MobileBy.AccessibilityId("AuthErrorLabel"));
        Assert.True(errorLabel.Displayed);
        Assert.Equal("Пожалуйста, введите корректный Email.", errorLabel.Text);
    }

    [Fact]
    public void ValidEmailShowsCodeInput()
    {
        if (_session == null)
        {
            return;
        }

        var emailInput = _session.FindElement(MobileBy.AccessibilityId("AuthEmailInput"));
        emailInput.Clear();
        emailInput.SendKeys("test@example.com");

        var getCodeButton = _session.FindElement(MobileBy.AccessibilityId("AuthGetCodeButton"));
        getCodeButton.Click();

        var codeInput = _session.FindElement(MobileBy.AccessibilityId("AuthCodeInput"));
        Assert.True(codeInput.Displayed);
    }

    [Fact]
    public void InvalidCodeShowsErrorLabel()
    {
        if (_session == null)
        {
            return;
        }

        var codeInput = _session.FindElement(MobileBy.AccessibilityId("AuthCodeInput"));
        codeInput.Clear();
        codeInput.SendKeys("123");

        var verifyCodeButton = _session.FindElement(MobileBy.AccessibilityId("AuthVerifyCodeButton"));
        verifyCodeButton.Click();

        var errorLabel = _session.FindElement(MobileBy.AccessibilityId("AuthErrorLabel"));
        Assert.True(errorLabel.Displayed);
        Assert.Equal("Пожалуйста, введите 6-значный код.", errorLabel.Text);
    }

    public void Dispose()
    {
        _session?.Quit();
        _session?.Dispose();
        _session = null;
        GC.SuppressFinalize(this);
    }
}
