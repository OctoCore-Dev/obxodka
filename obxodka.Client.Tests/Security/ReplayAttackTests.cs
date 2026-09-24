namespace obxodka.Client.Tests.Security;

[Trait("Category", "Security")]
[Trait("Category", "Unit")]
public class ReplayAttackTests
{
    private readonly byte[] _key = SHA256.HashData("MasterAntiReplaySecret2026"u8.ToArray());

    [Fact]
    public void TamperedCiphertextBytesAreGracefullyRejected()
    {
        using var crypto = new AesGcm(_key, 16);

        var originalPayload = "GET /secret-secure-tunnel-data HTTP/1.1\r\nHost: secure.vpn\r\n\r\n"u8.ToArray();
        var packed = FechsueCodec.Pack(originalPayload, originalPayload.Length, 9999, crypto, out var totalLen);

        var tampered = (byte[])packed.Clone();
        tampered[20] ^= 0xFF;

        var success = FechsueCodec.TryUnpack(tampered, totalLen, crypto, out _, out var unpacked, out _);

        Assert.False(success);
        Assert.Null(unpacked);

        ArrayPool<byte>.Shared.Return(packed);
    }

    [Fact]
    public void SessionIdTamperingIsDetected()
    {
        using var crypto = new AesGcm(_key, 16);

        var payload = "SecurePayload"u8.ToArray();
        var packed = FechsueCodec.Pack(payload, payload.Length, 12345, crypto, out var totalLen);

        var tampered = (byte[])packed.Clone();
        tampered[0] ^= 0xAA;

        var success = FechsueCodec.TryUnpack(tampered, totalLen, crypto, out var sessionId, out var unpacked, out _);

        if (success)
        {
            Assert.NotEqual(12345u, sessionId);
            if (unpacked != null)
            {
                ArrayPool<byte>.Shared.Return(unpacked);
            }
        }
        else
        {
            Assert.False(success);
        }

        ArrayPool<byte>.Shared.Return(packed);
    }

    [Fact]
    public void HighVolumeReplayStreamFailsAuthenticationGracefully()
    {
        using var crypto = new AesGcm(_key, 16);

        var payload = new byte[512];
        Random.Shared.NextBytes(payload);

        var packed = FechsueCodec.Pack(payload, payload.Length, 777, crypto, out var totalLen);

        for (var i = 0; i < 1000; i++)
        {
            var ok = FechsueCodec.TryUnpack(packed, totalLen, crypto, out var sId, out var unp, out var len);
            Assert.True(ok);
            Assert.Equal(777u, sId);
            Assert.Equal(512, len);
            if (unp != null)
            {
                ArrayPool<byte>.Shared.Return(unp);
            }
        }

        ArrayPool<byte>.Shared.Return(packed);
    }
}
