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
        Assert.Equal("Ошибка сервера 401: Unauthorized", error);
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

    [Fact]
    public async Task VerifyCodeAsyncWhenUnauthorizedDoesNotTriggerSessionExpiredEventAsync()
    {
        ApiService.ResetUnauthorizedState();
        var unauthorizedCount = 0;
        void OnUnauthorized() => Interlocked.Increment(ref unauthorizedCount);
        ApiService.OnUnauthorized += OnUnauthorized;

        try
        {
            using var response = new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.Unauthorized,
                Content = new StringContent("""{"message":"Неверный код подтверждения."}""", Encoding.UTF8, "application/json")
            };

            _ = _httpMessageHandlerMock
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(response);

            var request = new EmailVerifyRequest("test@example.com", "000000", "hwid", "Phone");
            var (success, data, error) = await _apiService.VerifyCodeAsync(request);

            Assert.False(success);
            Assert.Null(data);
            Assert.Equal("Неверный код подтверждения.", error);
            Assert.Equal(0, unauthorizedCount);
            Assert.False(ApiService.IsUnauthorizedPending);
        }
        finally
        {
            ApiService.OnUnauthorized -= OnUnauthorized;
            ApiService.ResetUnauthorizedState();
        }
    }

    [Fact]
    public async Task AuthorizedRequestsWhenUnauthorizedFireOnUnauthorizedOnlyOnceUntilReloginAsync()
    {
        ApiService.ResetUnauthorizedState();
        var unauthorizedCount = 0;
        void OnUnauthorized() => Interlocked.Increment(ref unauthorizedCount);
        ApiService.OnUnauthorized += OnUnauthorized;

        try
        {
            await AuthManager.SaveSessionAsync(new UserSession
            {
                Email = "user@example.com",
                IsLoggedIn = true,
                JwtToken = "expired-jwt-token"
            });

            _ = _httpMessageHandlerMock
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(() => new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.Unauthorized,
                    Content = new StringContent("", Encoding.UTF8, "application/json")
                });

            //три параллельных или последовательных 401 должны выдать событие ровно один раз
            _ = await _apiService.GetProfileAsync();
            _ = await _apiService.GetProfileAsync();
            _ = await _apiService.GetProfileAsync();

            Assert.Equal(1, unauthorizedCount);
            Assert.True(ApiService.IsUnauthorizedPending);

            //после очистки сессии повторный запрос без токена не должен снова вызывать onunauthorized
            await AuthManager.ClearSessionAsync();
            ApiService.ResetUnauthorizedState();
            _ = await _apiService.GetProfileAsync();
            Assert.Equal(1, unauthorizedCount);

            //после сохранения новой сессии защелка сбрасывается и может снова сработать один раз
            await AuthManager.SaveSessionAsync(new UserSession
            {
                Email = "user@example.com",
                IsLoggedIn = true,
                JwtToken = "new-jwt-token"
            });
            Assert.False(ApiService.IsUnauthorizedPending);

            _ = await _apiService.GetProfileAsync();
            Assert.Equal(2, unauthorizedCount);
        }
        finally
        {
            ApiService.OnUnauthorized -= OnUnauthorized;
            await AuthManager.ClearSessionAsync();
            ApiService.ResetUnauthorizedState();
        }
    }
}
