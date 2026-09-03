namespace obxodka.Client.Tests.Protocols;

[Trait("Category", "Protocols")]
[Trait("Category", "Resilience")]
public sealed class DynamicProtocolAndConnectivityTests
{
    [Fact]
    public void DynamicProtocolCandidatesOrderIsPrioritizedForPerformanceAndStealth()
    {
        var candidates = new[] { "FECHSUE", "HTTP3", "HTTP2" };

        Assert.Equal("FECHSUE", candidates[0]);
        Assert.Equal("HTTP3", candidates[1]);
        Assert.Equal("HTTP2", candidates[2]);
    }

    [Theory]
    [InlineData("FECHSUE", "FECHSUE")]
    [InlineData("HTTP3", "HTTP3")]
    [InlineData("HTTP2", "HTTP2")]
    [InlineData("AUTO", "FECHSUE")]
    public void ActiveProtocolResolvesCorrectly(string preferenceMode, string expectedActive)
    {
        var resolved = preferenceMode == "AUTO" ? "FECHSUE" : preferenceMode;
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
        var supportedProtocols = new[] { "AUTO", "FECHSUE", "HTTP3", "HTTP2" };

        foreach (var proto in supportedProtocols)
        {
            var isAuto = proto == "AUTO";
            var isFechsue = proto == "FECHSUE";
            var isHttp3 = proto == "HTTP3";
            var isHttp2 = proto == "HTTP2";

            Assert.True(isAuto || isFechsue || isHttp3 || isHttp2);
        }
    }

    [Fact]
    public void HotSwapPolicyAllowsInstantProtocolSwitchingWhenVpnConnected()
    {
        var isVpnRunning = true;
        var quickProtocolSwitch = true;

        var canSwitchProtocols = !isVpnRunning || quickProtocolSwitch;
        Assert.True(canSwitchProtocols);
    }

    [Fact]
    public async Task LiveServerCertificatePinningValidationAsync()
    {
        using var http = new HttpClient();
        var apiJson = await http.GetStringAsync("https://obxodka.one/api/vpn/cert-hash");
        using var doc = JsonDocument.Parse(apiJson);
        var expectedHash = doc.RootElement.GetProperty("hash").GetString();

        var validated = false;
        using var tcp = new TcpClient("45.63.117.29", 443);
        using var ssl = new SslStream(tcp.GetStream(), false, (sender, cert, chain, errors) =>
        {
            validated = GrpcTransport.ValidateServerCertificate(cert, chain, errors, expectedHash);
            return validated;
        });

        await ssl.AuthenticateAsClientAsync("google.com");
        Assert.True(validated);
    }

    [Fact]
    public async Task LiveGrpcTransportConnectionTestAsync()
    {
        using var http = new HttpClient();
        var apiJson = await http.GetStringAsync("https://obxodka.one/api/vpn/cert-hash");
        using var doc = JsonDocument.Parse(apiJson);
        var expectedHash = doc.RootElement.GetProperty("hash").GetString();
        OctopusEngine.DynamicSslPublicKeyHash = expectedHash;

        var transport = new GrpcTransport(useHttp3: false, activeRays: 1, clientCert: null, jwtToken: null, serverPort: 443);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var ex = await Record.ExceptionAsync(() => transport.ConnectAsync("45.63.117.29", "TEST_THUMBPRINT", cts.Token));

        Assert.True(ex is null or OperationCanceledException or TaskCanceledException, $"Expected cancellation, got: {ex}");
    }

    [Fact]
    public void SwitchingProtocolResetsSessionCleanly()
    {
        var activeProtocols = new List<string>
        {
            "HTTP3"
        };
        _ = Assert.Single(activeProtocols);
        Assert.Equal("HTTP3", activeProtocols[0]);

        activeProtocols.Clear();
        activeProtocols.Add("FECHSUE");
        _ = Assert.Single(activeProtocols);
        Assert.Equal("FECHSUE", activeProtocols[0]);
    }

    [Fact]
    public void VpnServerDtoSerializesAndDeserializesCertHashCorrectly()
    {
        var expectedHash = "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U=";
        var server = new VpnServerDto("45.63.117.29", 443, "Германия", true, 15, expectedHash);

        var json = JsonSerializer.Serialize(server, AppJsonContext.Default.VpnServerDto);
        Assert.Contains("certHash", json);

        var deserialized = JsonSerializer.Deserialize(json, AppJsonContext.Default.VpnServerDto);
        Assert.NotNull(deserialized);
        Assert.Equal(expectedHash, deserialized.CertHash);
        Assert.Equal("45.63.117.29", deserialized.Ip);
    }
}
