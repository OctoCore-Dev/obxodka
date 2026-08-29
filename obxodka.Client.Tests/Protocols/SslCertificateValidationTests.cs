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
}
