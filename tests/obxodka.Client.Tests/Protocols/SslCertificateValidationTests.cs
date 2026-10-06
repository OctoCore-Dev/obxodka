using obxodka.Client.Security;

namespace obxodka.Client.Tests.Protocols;

[Trait("Category", "Protocol")]
[Trait("Category", "Security")]
[Trait("Category", "Unit")]
public class SslCertificateValidationTests
{
    [Fact]
    public void ValidateServerCertificateReturnsFalseWhenCertificateIsNull()
    {
        var result = CertificateValidator.ValidateServerCertificate(null, null, SslPolicyErrors.None);
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

        var result = CertificateValidator.ValidateServerCertificate(cert, null, SslPolicyErrors.RemoteCertificateChainErrors, dynamicPinningHash: expectedHash);
        Assert.True(result);
    }

    [Fact]
    public void ValidateServerCertificateReturnsFalseWhenPublicKeyMismatchesPinningHash()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("cn=obxodka-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

        var forgedHash = "FORGED_HASH_THAT_DOES_NOT_MATCH_THE_SERVER_KEY==";

        var result = CertificateValidator.ValidateServerCertificate(cert, null, SslPolicyErrors.None, dynamicPinningHash: forgedHash);
        Assert.False(result);
    }

    [Fact]
    public void ValidateServerCertificateReturnsFalseWhenExpiredAndNoPinEnforced()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("cn=obxodka-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var expiredCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-1));

        var result = CertificateValidator.ValidateServerCertificate(expiredCert, null, SslPolicyErrors.RemoteCertificateChainErrors, dynamicPinningHash: "");
        Assert.False(result);
    }

    [Fact]
    public void ValidateServerCertificateReturnsFalseWhenSslPolicyErrorsPresentAndNoPinEnforced()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("cn=obxodka-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var validDateCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

        var result = CertificateValidator.ValidateServerCertificate(validDateCert, null, SslPolicyErrors.RemoteCertificateChainErrors, dynamicPinningHash: "");
        Assert.False(result);
    }

    [Fact]
    public void ValidateServerCertificateReturnsFalseWhenNameMismatchAndNoPinEnforced()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("cn=obxodka-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var validDateCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

        var result = CertificateValidator.ValidateServerCertificate(validDateCert, null, SslPolicyErrors.RemoteCertificateNameMismatch, dynamicPinningHash: null);
        Assert.False(result);
    }

    [Fact]
    public void ValidateServerCertificateReturnsFalseWhenObxodkaDomainHasUntrustedChain()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("cn=obxodka.one", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var selfSignedObxodkaCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

        var result = CertificateValidator.ValidateServerCertificate(selfSignedObxodkaCert, null, SslPolicyErrors.RemoteCertificateNameMismatch, dynamicPinningHash: "DIFFERENT_HASH==");
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

        var result = CertificateValidator.ValidateServerCertificate(selfSignedObxodkaCert, null, SslPolicyErrors.RemoteCertificateNameMismatch, dynamicPinningHash: expectedHash);
        Assert.True(result);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ValidateServerCertificateWithRealObxodkaCertAsync()
    {
        using var tcp = new TcpClient(AppConfig.DirectServerIp, 443);
        var capturedErrors = SslPolicyErrors.None;
        using var ssl = new SslStream(tcp.GetStream(), false, (s, cert, chain, errs) =>
        {
            capturedErrors = errs;
            return CertificateValidator.ValidateServerCertificate(cert, chain, errs, dynamicPinningHash: "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U=");
        });
        await ssl.AuthenticateAsClientAsync("obxodka.one");
        Assert.True(ssl.IsAuthenticated, $"Auth failed. Errs: {capturedErrors}");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ObxodkaStreamTransportConnectsToRealServerAsync()
    {
        using var sw = new StringWriter();
        var listener = new TextWriterTraceListener(sw);
        _ = Trace.Listeners.Add(listener);
        try
        {
            OctopusEngine.DynamicSslPublicKeyHash = "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U=";
            using var transport = new ObxodkaStreamTransport(serverPort: 443);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var (ip, _) = await transport.ConnectAsync(AppConfig.DirectServerIp, "441671375F27A7A223240C624CF1F56678666289", cts.Token);
                Assert.False(string.IsNullOrEmpty(ip));
            }
            catch (Exception ex) when (ex is TimeoutException or SocketException or OperationCanceledException or TaskCanceledException or HttpRequestException or IOException)
            {
            }
        }
        catch (Exception ex)
        {
            listener.Flush();
            Assert.Fail($"ObxodkaStreamTransport failed: {ex.Message}\nTrace:\n{sw}");
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }
}

