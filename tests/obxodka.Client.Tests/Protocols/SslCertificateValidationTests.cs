namespace obxodka.Client.Tests.Protocols;

[Collection("GlobalStaticState")]
[Trait("Category", "Protocol")]
[Trait("Category", "Security")]
[Trait("Category", "Unit")]
public class SslCertificateValidationTests
{
    [Fact]
    public void ValidateServerCertificateReturnsFalseWhenCertificateIsNull()
    {
        var result = CertificateValidator.ValidateServerCertificate(null);
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

        var result = CertificateValidator.ValidateServerCertificate(cert, null, SslPolicyErrors.RemoteCertificateChainErrors, expectedHash);
        Assert.True(result);
    }

    [Fact]
    public void ValidateServerCertificateRejectsWeakSha1ThumbprintAndAcceptsSha256Spki()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("cn=obxodka-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

        //слабый sha-1 thumbprint больше не принимается как пин
        var sha1Result = CertificateValidator.ValidateServerCertificate(cert, null, SslPolicyErrors.RemoteCertificateChainErrors, cert.Thumbprint);
        Assert.False(sha1Result);

        //sha-256 spki хэш принимается
        var spkiHash = Convert.ToBase64String(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
        var spkiResult = CertificateValidator.ValidateServerCertificate(cert, null, SslPolicyErrors.RemoteCertificateChainErrors, spkiHash);
        Assert.True(spkiResult);

        //резервные пины содержат только 32-байтовые sha-256 base64 хэши (44 символа)
        Assert.NotEmpty(AppSecrets.BackupPublicKeyHashes);
        Assert.All(AppSecrets.BackupPublicKeyHashes, pin => Assert.Equal(32, Convert.FromBase64String(pin).Length));
    }

    [Fact]
    public void ValidateServerCertificateReturnsFalseWhenPublicKeyMismatchesPinningHash()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("cn=obxodka-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

        var forgedHash = "FORGED_HASH_THAT_DOES_NOT_MATCH_THE_SERVER_KEY==";

        var result = CertificateValidator.ValidateServerCertificate(cert, null, SslPolicyErrors.None, forgedHash);
        Assert.False(result);
    }

    [Fact]
    public void ValidateServerCertificateReturnsFalseWhenExpiredAndNoPinEnforced()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("cn=obxodka-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var expiredCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-1));

        var result = CertificateValidator.ValidateServerCertificate(expiredCert, null, SslPolicyErrors.RemoteCertificateChainErrors, "");
        Assert.False(result);
    }

    [Fact]
    public void ValidateServerCertificateReturnsFalseWhenSslPolicyErrorsPresentAndNoPinEnforced()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("cn=obxodka-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var validDateCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

        var result = CertificateValidator.ValidateServerCertificate(validDateCert, null, SslPolicyErrors.RemoteCertificateChainErrors, "");
        Assert.False(result);
    }

    [Fact]
    public void ValidateServerCertificateReturnsFalseWhenNameMismatchAndNoPinEnforced()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("cn=obxodka-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var validDateCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

        var result = CertificateValidator.ValidateServerCertificate(validDateCert, null, SslPolicyErrors.RemoteCertificateNameMismatch);
        Assert.False(result);
    }

    [Fact]
    public void ValidateServerCertificateReturnsFalseWhenObxodkaDomainHasUntrustedChain()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"cn={AppConfig.PrimaryDomain}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var selfSignedObxodkaCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

        var result = CertificateValidator.ValidateServerCertificate(selfSignedObxodkaCert, null,
            SslPolicyErrors.RemoteCertificateNameMismatch, "DIFFERENT_HASH==");
        Assert.False(result);
    }

    [Fact]
    public void ValidateServerCertificateReturnsTrueWhenObxodkaDomainMatchesPinning()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"cn={AppConfig.PrimaryDomain}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var selfSignedObxodkaCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

        var pubKey = selfSignedObxodkaCert.GetPublicKey();
        var expectedHash = Convert.ToBase64String(SHA256.HashData(pubKey));

        var result = CertificateValidator.ValidateServerCertificate(selfSignedObxodkaCert, null,
            SslPolicyErrors.RemoteCertificateNameMismatch, expectedHash);
        Assert.True(result);
    }

    [Fact]
    public void ValidateServerCertificateReturnsFalseWhenOctopusEnginePinMismatchesAndDynamicPinIsNull()
    {
        var prevPin = OctopusEngine.DynamicSslPublicKeyHash;
        try
        {
            OctopusEngine.DynamicSslPublicKeyHash = "DIFFERENT_HASH_IN_OCTOPUS==";
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest($"cn={AppConfig.PrimaryDomain}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

            var result = CertificateValidator.ValidateServerCertificate(cert);
            Assert.False(result);
        }
        finally
        {
            OctopusEngine.DynamicSslPublicKeyHash = prevPin;
        }
    }

    [Fact]
    public void ValidateServerCertificateReturnsFalseWhenSubjectContainsAttackerSuffix()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"cn={AppConfig.PrimaryDomain}.attacker.com", rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

        var result = CertificateValidator.ValidateServerCertificate(cert, null, SslPolicyErrors.RemoteCertificateNameMismatch);
        Assert.False(result);
    }

    [Fact]
    public void ValidateServerCertificateRejectsNameMismatchForUntrustedExpectedHost()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"cn={AppConfig.PrimaryDomain}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

        var result = CertificateValidator.ValidateServerCertificate(
            cert,
            null,
            SslPolicyErrors.RemoteCertificateNameMismatch,
            "",
            "evil.example.com");
        Assert.False(result);
    }

    [Fact]
    public void IsTrustedObxodkaCertificateMatchesWildcardSanForConfiguredMirrorSubdomains()
    {
        var mirrorWithSubdomain = BootstrapConfig.Mirrors.FirstOrDefault(m => m.Count(c => c == '.') >= 2);
        Assert.False(string.IsNullOrWhiteSpace(mirrorWithSubdomain));

        var dotIndex = mirrorWithSubdomain.IndexOf('.');
        var wildcard = "*" + mirrorWithSubdomain[dotIndex..];

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"cn={mirrorWithSubdomain[(dotIndex + 1)..]}", rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(wildcard);
        sanBuilder.AddDnsName(mirrorWithSubdomain[(dotIndex + 1)..]);
        req.CertificateExtensions.Add(sanBuilder.Build());
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

        Assert.True(CertificateValidator.IsTrustedObxodkaCertificate(cert));
    }

    [Fact]
    public void IsTrustedObxodkaCertificateRejectsAttackerWildcardSan()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("cn=attacker.example", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("*.attacker.example");
        sanBuilder.AddDnsName($"*.{AppConfig.PrimaryDomain}.attacker.example");
        req.CertificateExtensions.Add(sanBuilder.Build());
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

        Assert.False(CertificateValidator.IsTrustedObxodkaCertificate(cert));
    }

    [Fact]
    public void PopulateExtraStoreCopiesIntermediatesFromSourceExtraStore()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("cn=intermediate-ca", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var intermediate = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

        using var sourceChain = new X509Chain();
        _ = sourceChain.ChainPolicy.ExtraStore.Add(intermediate);

        using var targetChain = new X509Chain();
        CertificateValidator.PopulateExtraStore(targetChain, sourceChain);

        Assert.Contains(targetChain.ChainPolicy.ExtraStore, c => c.Thumbprint == intermediate.Thumbprint);
    }

    [Fact]
    public void AppLoggerFormatExceptionDetailsUnwrapsNestedAggregateExceptionWithStackTrace()
    {
        Exception captured;
        try
        {
            ThrowInnerNullRef();
            return;
        }
        catch (Exception ex)
        {
            captured = new AggregateException(new AggregateException(ex));
        }

        var formatted = AppLogger.FormatExceptionDetails(captured);
        Assert.Contains("NullReferenceException", formatted);
        Assert.Contains("simulated-nre", formatted);
        Assert.Contains(nameof(ThrowInnerNullRef), formatted);
        return;

        static void ThrowInnerNullRef() => throw new NullReferenceException("simulated-nre");
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
            return CertificateValidator.ValidateServerCertificate(cert, chain, errs, "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U=");
        });
        await ssl.AuthenticateAsClientAsync(AppConfig.PrimaryDomain);
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
            using var transport = new ObxodkaStreamTransport();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var (ip, _) = await transport.ConnectAsync(AppConfig.DirectServerIp, "441671375F27A7A223240C624CF1F56678666289", cts.Token);
                Assert.False(string.IsNullOrEmpty(ip));
            }
            catch (Exception ex) when (ex is TimeoutException or SocketException or OperationCanceledException or TaskCanceledException
                                           or HttpRequestException or IOException)
            {
                //внешний сервер недоступен в офлайн или тестовой среде
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
