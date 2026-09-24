namespace obxodka.Client.Tests.Protocols;

[Trait("Category", "Protocol")]
[Trait("Category", "Security")]
[Trait("Category", "Unit")]
public class SslCertificateValidationTests
{
    [Fact]
    public void ValidateServerCertificateReturnsFalseWhenCertificateIsNull()
    {
        var result = GrpcTransport.ValidateServerCertificate(null, null, SslPolicyErrors.None);
        Assert.False(result);
    }

    [Fact]
    public void ValidateServerCertificateReturnsTrueWhenPublicKeyMatchesPinningHash()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("cn=obxodka-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

        var pubKey = cert.GetPublicKey();
        var expectedHash = Convert.ToBase64String(SHA256.HashData(pubKey));

        var result = GrpcTransport.ValidateServerCertificate(cert, null, SslPolicyErrors.RemoteCertificateChainErrors, dynamicPinningHash: expectedHash);
        Assert.True(result);
    }

    [Fact]
    public void ValidateServerCertificateReturnsFalseWhenPublicKeyMismatchesPinningHash()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("cn=obxodka-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

        var forgedHash = "FORGED_HASH_THAT_DOES_NOT_MATCH_THE_SERVER_KEY==";

        var result = GrpcTransport.ValidateServerCertificate(cert, null, SslPolicyErrors.None, dynamicPinningHash: forgedHash);
        Assert.False(result);
    }

    [Fact]
    public void ValidateServerCertificateReturnsFalseWhenExpiredAndNoPinEnforced()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("cn=obxodka-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var expiredCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-1));

        var result = GrpcTransport.ValidateServerCertificate(expiredCert, null, SslPolicyErrors.RemoteCertificateChainErrors, dynamicPinningHash: "");
        Assert.False(result);
    }

    [Fact]
    public void ValidateServerCertificateReturnsFalseWhenSslPolicyErrorsPresentAndNoPinEnforced()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("cn=obxodka-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var validDateCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

        var result = GrpcTransport.ValidateServerCertificate(validDateCert, null, SslPolicyErrors.RemoteCertificateChainErrors, dynamicPinningHash: "");
        Assert.False(result);
    }

    [Fact]
    public void ValidateServerCertificateReturnsFalseWhenNameMismatchAndNoPinEnforced()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("cn=obxodka-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var validDateCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

        var result = GrpcTransport.ValidateServerCertificate(validDateCert, null, SslPolicyErrors.RemoteCertificateNameMismatch, dynamicPinningHash: null);
        Assert.False(result);
    }

    [Fact]
    public void ValidateServerCertificateReturnsFalseWhenObxodkaDomainHasUntrustedChain()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("cn=obxodka.one", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var selfSignedObxodkaCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

        var result = GrpcTransport.ValidateServerCertificate(selfSignedObxodkaCert, null, SslPolicyErrors.RemoteCertificateNameMismatch, dynamicPinningHash: "DIFFERENT_HASH==");
        Assert.False(result);
    }

    [Fact]
    public void ValidateServerCertificateReturnsTrueWhenObxodkaDomainMatchesPinning()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("cn=obxodka.one", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var selfSignedObxodkaCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

        var pubKey = selfSignedObxodkaCert.GetPublicKey();
        var expectedHash = Convert.ToBase64String(SHA256.HashData(pubKey));

        var result = GrpcTransport.ValidateServerCertificate(selfSignedObxodkaCert, null, SslPolicyErrors.RemoteCertificateNameMismatch, dynamicPinningHash: expectedHash);
        Assert.True(result);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ValidateServerCertificateWithRealObxodkaCertAsync()
    {
        using var tcp = new System.Net.Sockets.TcpClient("45.63.117.29", 443);
        var capturedErrors = SslPolicyErrors.None;
        using var ssl = new SslStream(tcp.GetStream(), false, (s, cert, chain, errs) =>
        {
            capturedErrors = errs;
            return GrpcTransport.ValidateServerCertificate(cert, chain, errs, dynamicPinningHash: "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U=");
        });
        await ssl.AuthenticateAsClientAsync("obxodka.one");
        Assert.True(ssl.IsAuthenticated, $"Auth failed. Errs: {capturedErrors}");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GrpcTransportConnectsToRealServerAsync()
    {
        using var sw = new System.IO.StringWriter();
        var listener = new TextWriterTraceListener(sw);
        _ = Trace.Listeners.Add(listener);
        try
        {
            OctopusEngine.DynamicSslPublicKeyHash = "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U=";
            using var transport = new GrpcTransport(useHttp3: false, activeRays: 8, clientCert: null, jwtToken: null, serverPort: 443);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var (ip, _) = await transport.ConnectAsync("45.63.117.29", "441671375F27A7A223240C624CF1F56678666289", cts.Token);
            Assert.False(string.IsNullOrEmpty(ip));
        }
        catch (Exception ex)
        {
            listener.Flush();
            Assert.Fail($"GrpcTransport failed: {ex.Message}\nTrace:\n{sw}");
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }
}

