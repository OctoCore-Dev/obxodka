namespace obxodka.Client.Tests;

[Trait("Category", "Unit")]
public partial class ApiServiceTests : IDisposable
{
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private readonly HttpClient _httpClient;
    private readonly ApiService _apiService;

    public ApiServiceTests()
    {
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_httpMessageHandlerMock.Object)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };
        _apiService = new ApiService(_httpClient);
    }

    [Fact]
    public async Task RequestCodeAsyncWhenSuccessfulReturnsTrueAsync()
    {
        var request = new EmailAuthRequest("test@example.com");

        _ = _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("", Encoding.UTF8, "application/json")
            });

        var (success, error) = await _apiService.RequestCodeAsync(request);

        Assert.True(success);
        Assert.Null(error);
    }

    [Fact]
    public async Task RequestCodeAsyncWhenUnauthorizedReturnsFalseAndErrorMessageAsync()
    {
        var request = new EmailAuthRequest("test@example.com");

        _ = _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.Unauthorized,
                Content = new StringContent("", Encoding.UTF8, "application/json")
            });

        var (success, error) = await _apiService.RequestCodeAsync(request);

        Assert.False(success);
        Assert.Equal("Сессия истекла или устройство было удалено.", error);
    }

    [Fact]
    public async Task RequestCodeAsyncWhenBadRequestParsesErrorResponseAsync()
    {
        var request = new EmailAuthRequest("test@example.com");

        _ = _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.BadRequest,
                Content = new StringContent(/*lang=json,strict*/ "{\"message\":\"Некорректный email.\"}", Encoding.UTF8, "application/json")
            });

        var (success, error) = await _apiService.RequestCodeAsync(request);

        Assert.False(success);
        Assert.Equal("Некорректный email.", error);
    }

    [Fact]
    public async Task VerifyCodeAsyncWhenValidReturnsLoginResponseAsync()
    {
        var request = new EmailVerifyRequest("test@example.com", "123456", "test_hwid", "MyPhone");
        var rawJson = /*lang=json,strict*/ "{\"token\":\"jwt_token_sample\",\"vpnConfig\":\"vpn_config_data\",\"certThumbprint\":\"CERT_123\",\"balanceSeconds\":3600,\"email\":\"test@example.com\"}";

        _ = _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(rawJson, Encoding.UTF8, "application/json")
            });

        var (success, data, error) = await _apiService.VerifyCodeAsync(request);

        Assert.True(success);
        Assert.Null(error);
        Assert.NotNull(data);
        Assert.Equal("test@example.com", data.Email);
        Assert.Equal("jwt_token_sample", data.Token);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
