namespace obxodka.Client.Tests.Telemetry;

[Trait("Category", "Unit")]
public sealed class TelemetryFilterTests
{
    public TelemetryFilterTests() => TelemetryFilter.ResetHistory();

    [Theory]
    [InlineData("HTTP 429: Too Many Requests")]
    [InlineData("Server returned 429 TooManyRequests (Rate limit)")]
    [InlineData("Ошибка 429: слишком много запросов")]
    public void ShouldSendToServer_FiltersOutRateLimitNoise(string message)
    {
        var result = TelemetryFilter.ShouldSendToServer(message);
        Assert.False(result);
    }

    [Theory]
    [InlineData("HTTP 401: Unauthorized")]
    [InlineData("401 token expired during logout")]
    public void ShouldSendToServer_FiltersOutAuthExpirationNoise(string message)
    {
        var result = TelemetryFilter.ShouldSendToServer(message);
        Assert.False(result);
    }

    [Fact]
    public void ShouldSendToServer_FiltersOutCancellationExceptions()
    {
        var canceledEx = new OperationCanceledException("The operation was canceled.");
        var taskCanceledEx = new TaskCanceledException();

        Assert.False(TelemetryFilter.ShouldSendToServer("User stopped VPN", canceledEx));
        Assert.False(TelemetryFilter.ShouldSendToServer("Timeout abort", taskCanceledEx));
        Assert.False(TelemetryFilter.ShouldSendToServer("OperationCanceledException in stream"));
    }

    [Fact]
    public void ShouldSendToServer_FiltersOutAbortedSockets()
    {
        var socketEx = new SocketException((int)SocketError.OperationAborted);
        Assert.False(TelemetryFilter.ShouldSendToServer("Socket closed", socketEx));
    }

    [Fact]
    public void ShouldSendToServer_AllowsGenuineErrors()
    {
        var cryptoEx = new CryptographicException("Failed to decrypt server payload");
        Assert.True(TelemetryFilter.ShouldSendToServer("Decryption error", cryptoEx));

        var nullRefEx = new NullReferenceException("Wintun session was null");
        Assert.True(TelemetryFilter.ShouldSendToServer("Wintun failure", nullRefEx));
    }

    [Fact]
    public void ShouldSendToServer_DeduplicatesIdenticalErrorsWithinWindow()
    {
        var message = "Repeated tunnel socket failure #1";
        var ex = new InvalidOperationException("Fatal pipeline break");

        var first = TelemetryFilter.ShouldSendToServer(message, ex);
        Assert.True(first);

        var second = TelemetryFilter.ShouldSendToServer(message, ex);
        Assert.False(second);
    }
}
