namespace obxodka.Client.Tests;

[Trait("Category", "Unit")]
public class ApiServiceTests : IDisposable
{
    private readonly ApiService _apiService;
    private readonly HttpClient _httpClient;
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;

    public ApiServiceTests()
    {
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_httpMessageHandlerMock.Object)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };
        _apiService = new ApiService(_httpClient);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RequestCodeAsyncWhenSuccessfulReturnsTrueAsync()
    {
        var request = new EmailAuthRequest("test@example.com");

        using var response = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("", Encoding.UTF8, "application/json")
        };
        _ = _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(response);

        var (success, error) = await _apiService.RequestCodeAsync(request);

        Assert.True(success);
        Assert.Null(error);
    }

    [Fact]
    public async Task RequestCodeAsyncWhenUnauthorizedReturnsFalseAndErrorMessageAsync()
    {
        var request = new EmailAuthRequest("test@example.com");

        using var response = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.Unauthorized,
            Content = new StringContent("", Encoding.UTF8, "application/json")
        };
        _ = _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(response);

        var (success, error) = await _apiService.RequestCodeAsync(request);

        Assert.False(success);
        Assert.Equal("Сессия истекла или устройство было удалено.", error);
    }

    [Fact]
    public async Task RequestCodeAsyncWhenBadRequestParsesErrorResponseAsync()
    {
        var request = new EmailAuthRequest("test@example.com");

        using var response = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.BadRequest,
            Content = new StringContent( /*lang=json,strict*/ "{\"message\":\"Некорректный email.\"}", Encoding.UTF8, "application/json")
        };
        _ = _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(response);

        var (success, error) = await _apiService.RequestCodeAsync(request);

        Assert.False(success);
        Assert.Equal("Некорректный email.", error);
    }

    [Fact]
    public async Task VerifyCodeAsyncWhenValidReturnsLoginResponseAsync()
    {
        var request = new EmailVerifyRequest("test@example.com", "123456", "test_hwid", "MyPhone");
        var rawJson = /*lang=json,strict*/
            "{\"token\":\"jwt_token_sample\",\"vpnConfig\":\"vpn_config_data\",\"certThumbprint\":\"CERT_123\",\"balanceSeconds\":3600,\"email\":\"test@example.com\"}";

        using var response = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(rawJson, Encoding.UTF8, "application/json")
        };
        _ = _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(response);

        var (success, data, error) = await _apiService.VerifyCodeAsync(request);

        Assert.True(success);
        Assert.Null(error);
        Assert.NotNull(data);
        Assert.Equal("test@example.com", data.Email);
        Assert.Equal("jwt_token_sample", data.Token);
    }

    [Fact]
    public async Task GeneratePaymentLinkAsyncIncludesCurrencyAndMethodAsync()
    {
        Uri? capturedUri = null;
        using var response = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("""{"url":"https://app.lava.top/test"}""", Encoding.UTF8, "application/json")
        };

        _ = _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUri = req.RequestUri)
            .ReturnsAsync(response);

        var (success, url, error) = await _apiService.GeneratePaymentLinkAsync(10m, "USD", TariffCalculator.MethodPayPal);

        Assert.True(success);
        Assert.Null(error);
        Assert.Equal("https://app.lava.top/test", url);
        Assert.NotNull(capturedUri);
        Assert.Contains("amount=10", capturedUri.Query);
        Assert.Contains("currency=USD", capturedUri.Query);
        Assert.Contains("method=PAYPAL", capturedUri.Query);
    }

    [Fact]
    public async Task SyncPendingPaymentsAsyncDeserializesResponseAsync()
    {
        using var response = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("""{"credited":true,"balanceSeconds":2703600,"pendingCount":0}""", Encoding.UTF8,
                "application/json")
        };

        _ = _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        var (success, data, error) = await _apiService.SyncPendingPaymentsAsync();

        Assert.True(success);
        Assert.Null(error);
        Assert.NotNull(data);
        Assert.True(data.Credited);
        Assert.Equal(2703600L, data.BalanceSeconds);
        Assert.Equal(0, data.PendingCount);
    }
}
