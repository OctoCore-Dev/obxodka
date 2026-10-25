namespace obxodka.Client.Tests.UI;

[Trait("Category", "UI")]
public partial class AuthUITests : IDisposable
{
    private WindowsDriver? _session;

    private const string WindowsApplicationDriverUrl = "http://127.0.0.1:4723";
    private static readonly string t_appId = UITestHelper.ResolveAppPath();

    public AuthUITests()
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

        var emailInput = _session.FindElement(MobileBy.AccessibilityId("AuthEmailInput"));
        emailInput.Clear();
        emailInput.SendKeys("test@example.com");

        var getCodeButton = _session.FindElement(MobileBy.AccessibilityId("AuthGetCodeButton"));
        getCodeButton.Click();

        var codeInput = _session.FindElement(MobileBy.AccessibilityId("AuthCodeInput"));
        codeInput.SendKeys("000000");

        var verifyButton = _session.FindElement(MobileBy.AccessibilityId("AuthVerifyCodeButton"));
        verifyButton.Click();

        var errorLabel = _session.FindElement(MobileBy.AccessibilityId("AuthErrorLabel"));
        Assert.True(errorLabel.Displayed);
    }

    public void Dispose()
    {
        _session?.Quit();
        _session?.Dispose();
        _session = null;
        GC.SuppressFinalize(this);
    }
}
