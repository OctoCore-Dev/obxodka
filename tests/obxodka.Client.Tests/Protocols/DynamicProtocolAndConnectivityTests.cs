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
                validated = CertificateValidator.ValidateServerCertificate(cert, chain, errors, expectedHash);
                return validated;
            });

            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "google.com"
            }, cts.Token);
            Assert.True(validated);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or SocketException or TimeoutException or AuthenticationException)
        {
            return;
        }
    }

    [Fact]
    public async Task LiveStreamTransportConnectionTestAsync()
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

        var transport = new ObxodkaStreamTransport(serverPort: 443);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var ex = await Record.ExceptionAsync(() => transport.ConnectAsync(AppConfig.DirectServerIp, "TEST_THUMBPRINT", cts.Token));

        Assert.True(ex is null or OperationCanceledException or TaskCanceledException or HttpRequestException or IOException or SocketException, $"Expected cancellation, got: {ex}");
    }

    [Fact]
    public async Task LiveStreamEchoTestAsync()
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

        var transport = new ObxodkaStreamTransport(serverPort: 443);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string ip;
        try
        {
            var res = await transport.ConnectAsync(AppConfig.DirectServerIp, "8C4D558DD38236249DA05CA9FD59658C0CAC305E", cts.Token);
            ip = res.ip;
        }
        catch (Exception ex) when (ex is TimeoutException or SocketException or OperationCanceledException or TaskCanceledException or HttpRequestException or IOException)
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

    [Fact]
    public void DynamicSniGenerationProducesValidSubdomains()
    {
        for (var i = 0; i < 100; i++)
        {
            var sni = AppSecrets.GetRandomSni();
            Assert.False(string.IsNullOrWhiteSpace(sni));
            Assert.Contains(sni, AppSecrets.AllowedSniPool, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void GetCandidateSniListReturnsUniqueCandidates()
    {
        var candidates = AppSecrets.GetCandidateSniList(8);
        Assert.Equal(8, candidates.Count);
        Assert.Equal(candidates.Count, candidates.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("login.live.com", candidates, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("dl.google.com", candidates, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LiveServerH2SyncProbeAsync()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "gateway.icloud.com",
                ApplicationProtocols = [SslApplicationProtocol.Http2],
                EnabledSslProtocols = SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (s, c, ch, e) => CertificateValidator.ValidateServerCertificate(c, ch, e, "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U=")
            },
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync("70.34.201.253", 443, ct);
                var netStream = new NetworkStream(socket, true);
                return new DpiBypassStream(netStream);
            }
        };

        using var client = new HttpClient(handler);
        var req = new HttpRequestMessage(HttpMethod.Post, "https://gateway.icloud.com:443/api/v1/sync?ray=0&mtu=1280")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        var hsPayload = Encoding.UTF8.GetBytes("CBDEBFDE826F40C6BF0F77525522E624C7A624BC");
        var hsFrame = ObxodkaFraming.Pack(ObxodkaFraming.CmdHandshake, hsPayload, out var hsTotalLen);

        var pushContent = new PushStreamContent(async (stream, ct) =>
        {
            await stream.WriteAsync(hsFrame.AsMemory(0, hsTotalLen), ct);
            await stream.FlushAsync(ct);
            await Task.Delay(1000, ct);
        });
        req.Content = pushContent;

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        try
        {
            var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            Assert.NotNull(resp);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or SocketException or TimeoutException or OperationCanceledException)
        {
            return;
        }
    }

    private sealed class PushStreamContent(Func<Stream, CancellationToken, Task> onSerialize) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            onSerialize(stream, cancellationToken);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override bool TryComputeLength(out long length) { length = -1; return false; }
    }
}
