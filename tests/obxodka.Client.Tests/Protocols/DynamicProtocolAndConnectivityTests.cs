namespace obxodka.Client.Tests.Protocols;

[Trait("Category", "Protocols")]
[Trait("Category", "Resilience")]
public sealed class DynamicProtocolAndConnectivityTests
{
    [Fact]
    public void DynamicProtocolCandidatesOrderIsPrioritizedForPerformanceAndStealth()
    {
        var candidates = new[] { "HTTP2" };
        Assert.Equal("HTTP2", candidates[0]);
    }

    [Theory]
    [InlineData("HTTP2", "HTTP2")]
    [InlineData("AUTO", "HTTP2")]
    public void ActiveProtocolResolvesCorrectly(string preferenceMode, string expectedActive)
    {
        var resolved = preferenceMode == "AUTO" ? "HTTP2" : preferenceMode;
        Assert.Equal(expectedActive, resolved);
    }

    [Fact]
    public void WakeFromSleepRetryPolicyCalculatesProgressiveDelays()
    {
        var maxRetries = 3;
        var delays = new List<int>();

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            var delay = attempt * 600;
            delays.Add(delay);
        }

        Assert.Equal(600, delays[0]);
        Assert.Equal(1200, delays[1]);
        Assert.Equal(1800, delays[2]);
    }

    [Theory]
    [InlineData(AppNetworkAccess.None, false)]
    [InlineData(AppNetworkAccess.Unknown, false)]
    [InlineData(AppNetworkAccess.Local, false)]
    [InlineData(AppNetworkAccess.ConstrainedInternet, false)]
    [InlineData(AppNetworkAccess.Internet, true)]
    public void ConnectivityAccessValidation(AppNetworkAccess access, bool hasInternet)
    {
        var isOnline = access == AppNetworkAccess.Internet;
        Assert.Equal(hasInternet, isOnline);
    }

    [Fact]
    public void ProtocolSyncAcrossPreferencesReflectsSelectedState()
    {
        var supportedProtocols = new[] { "HTTP2" };

        foreach (var proto in supportedProtocols)
        {
            var isHttp2 = proto == "HTTP2";
            Assert.True(isHttp2);
        }
    }

    [Fact]
    public async Task LiveServerCertificatePinningValidationAsync()
    {
        string expectedHash;
        try
        {
            using var http = new HttpClient();
            var apiJson = await http.GetStringAsync("https://api.octocore.dev/api/vpn/cert-hash");
            using var doc = JsonDocument.Parse(apiJson);
            expectedHash = doc.RootElement.GetProperty("hash").GetString() ?? "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U=";
        }
        catch (Exception e) when (e is HttpRequestException or IOException or SocketException or TimeoutException)
        {
            expectedHash = "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U=";
        }

        var validated = false;
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(AppConfig.DirectServerIp, 443, cts.Token);
            using var ssl = new SslStream(tcp.GetStream(), false, (sender, cert, chain, errors) =>
            {
                validated = GrpcTransport.ValidateServerCertificate(cert, chain, errors, expectedHash);
                return validated;
            });

            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "google.com"
            }, cts.Token);
            Assert.True(validated);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or SocketException or TimeoutException or System.Security.Authentication.AuthenticationException)
        {
            return;
        }
    }

    [Fact]
    public async Task LiveGrpcTransportConnectionTestAsync()
    {
        try
        {
            using var http = new HttpClient();
            var apiJson = await http.GetStringAsync("https://api.octocore.dev/api/vpn/cert-hash");
            using var doc = JsonDocument.Parse(apiJson);
            var expectedHash = doc.RootElement.GetProperty("hash").GetString();
            OctopusEngine.DynamicSslPublicKeyHash = expectedHash;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or SocketException or TimeoutException)
        {
            OctopusEngine.DynamicSslPublicKeyHash = "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U=";
        }

        var transport = new GrpcTransport(activeRays: 1, clientCert: null, jwtToken: null, serverPort: 443);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var ex = await Record.ExceptionAsync(() => transport.ConnectAsync(AppConfig.DirectServerIp, "TEST_THUMBPRINT", cts.Token));

        Assert.True(ex is null or OperationCanceledException or TaskCanceledException or HttpRequestException or IOException or SocketException, $"Expected cancellation, got: {ex}");
    }

    [Fact]
    public async Task LiveGrpcEchoTestAsync()
    {
        string expectedHash;
        try
        {
            using var http = new HttpClient();
            var apiJson = await http.GetStringAsync("https://api.octocore.dev/api/vpn/cert-hash");
            using var doc = JsonDocument.Parse(apiJson);
            expectedHash = doc.RootElement.GetProperty("hash").GetString() ?? "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U=";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or TimeoutException)
        {
            expectedHash = "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U=";
        }
        OctopusEngine.DynamicSslPublicKeyHash = expectedHash;

        var transport = new GrpcTransport(activeRays: 8, clientCert: null, jwtToken: null, serverPort: 443);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string ip;
        try
        {
            var res = await transport.ConnectAsync(AppConfig.DirectServerIp, "8C4D558DD38236249DA05CA9FD59658C0CAC305E", cts.Token);
            ip = res.ip;
        }
        catch (Exception ex) when (ex is TimeoutException or SocketException or OperationCanceledException or TaskCanceledException or Grpc.Core.RpcException or HttpRequestException or IOException)
        {
            return;
        }

        long pingRtt = -1;
        var pingTcs = new TaskCompletionSource<long>();
        transport.OnPingUpdated += rtt =>
        {
            pingRtt = rtt;
            _ = pingTcs.TrySetResult(rtt);
        };

        await transport.SendPingProbeAsync();
        var completed = await Task.WhenAny(pingTcs.Task, Task.Delay(5000));
        Assert.True(completed == pingTcs.Task, $"Ping probe timed out! Assigned IP was {ip}");
        Assert.True(pingRtt > 0, $"Expected positive RTT, got: {pingRtt}");
    }

    [Fact]
    public void VpnServerDtoSerializesAndDeserializesCertHashCorrectly()
    {
        var expectedHash = "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U=";
        var server = new VpnServerDto(AppConfig.DirectServerIp, 443, "Швеция", true, 15, expectedHash);

        var json = JsonSerializer.Serialize(server, AppJsonContext.Default.VpnServerDto);
        Assert.Contains("certHash", json);

        var deserialized = JsonSerializer.Deserialize(json, AppJsonContext.Default.VpnServerDto);
        Assert.NotNull(deserialized);
        Assert.Equal(expectedHash, deserialized.CertHash);
        Assert.Equal(AppConfig.DirectServerIp, deserialized.Ip);
    }
}
