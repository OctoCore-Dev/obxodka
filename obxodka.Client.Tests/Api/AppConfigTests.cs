namespace obxodka.Client.Tests;

[Trait("Category", "Unit")]
public class AppConfigTests
{
    [Fact]
    public void DefaultApiBaseUrlIsValidHttpsUrl()
    {
        Assert.StartsWith("https://", AppConfig.DefaultApiBaseUrl);
        Assert.True(Uri.TryCreate(AppConfig.DefaultApiBaseUrl, UriKind.Absolute, out var uri));
        Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
    }

    [Theory]
    [InlineData("api/Auth/me", "https://obxodka.one/api/Auth/me")]
    [InlineData("/api/Auth/me", "https://obxodka.one/api/Auth/me")]
    [InlineData("api/payment/generate", "https://obxodka.one/api/payment/generate")]
    public void ApiUrlFormatsCorrectly(string endpoint, string expected)
    {
        AppConfig.ApiBaseUrl = AppConfig.DefaultApiBaseUrl;
        var url = AppConfig.ApiUrl(endpoint);
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ApiUrlWithEmptyEndpointThrowsArgumentException(string invalidEndpoint) => Assert.Throws<ArgumentException>(() => AppConfig.ApiUrl(invalidEndpoint));

    [Fact]
    public void CustomBaseUrlOverridesDefaultAndTrims()
    {
        try
        {
            AppConfig.ApiBaseUrl = "https://custom.vpn.server/  ";
            var url = AppConfig.ApiUrl("api/status");
            Assert.Equal("https://custom.vpn.server/api/status", url);
        }
        finally
        {
            AppConfig.ApiBaseUrl = AppConfig.DefaultApiBaseUrl;
        }
    }
}
