namespace obxodka.Client.Tests;

[Trait("Category", "Unit")]
public class FechsueCodecTests
{
    private readonly byte[] _key = SHA256.HashData("TestMasterKeySecret123"u8.ToArray());

    [Fact]
    public void PackAndUnpackAuthValidThumbprintSucceeds()
    {
        var thumbprint = "A1B2C3D4E5F678901234567890ABCDEF12345678";
        byte streamIndex = 2;

        var packed = FechsueCodec.PackAuth(thumbprint, streamIndex, out var totalLen);
        Assert.NotNull(packed);
        Assert.True(totalLen > 0);

        var success = FechsueCodec.TryUnpackAuth(packed.AsSpan(0, totalLen), out var unpackedThumbprint, out var sessionId, out var unpackedStream);

        Assert.True(success);
        Assert.Equal(thumbprint, unpackedThumbprint);
        Assert.Equal(streamIndex, unpackedStream);
        Assert.NotEqual(0u, sessionId);
    }

    [Fact]
    public void UnpackAuthInvalidTokenFails()
    {
        var buffer = new byte[64];
        buffer[0] = 0xAA;
        var success = FechsueCodec.TryUnpackAuth(buffer, out _, out _, out _);
        Assert.False(success);
    }

    [Fact]
    public void PackAndUnpackDiscSucceeds()
    {
        var thumbprint = "A1B2C3D4E5F678901234567890ABCDEF12345678";
        var packed = FechsueCodec.PackDisc(thumbprint, out var totalLen);
        Assert.NotNull(packed);
        Assert.True(totalLen > 0);

        var success = FechsueCodec.TryUnpackDisc(packed.AsSpan(0, totalLen), out var unpackedThumbprint, out var sessionId);
        Assert.True(success);
        Assert.Equal(thumbprint, unpackedThumbprint);
        Assert.NotEqual(0u, sessionId);
    }

    [Fact]
    public void PackAndUnpackDataPayloadIntegrityVerified()
    {
        using var crypto = new AesGcm(_key, 16);
        uint sessionId = 0x12345678;

        var originalPayload = Encoding.UTF8.GetBytes("Super secret encrypted VPN packet payload data!");
        var packed = FechsueCodec.Pack(originalPayload, originalPayload.Length, sessionId, crypto, out var totalLen);

        Assert.NotNull(packed);
        Assert.True(totalLen > originalPayload.Length);

        var success = FechsueCodec.TryUnpack(packed, totalLen, crypto, out var unpackedSession, out var unpackedPayload, out var realLen);

        Assert.True(success);
        Assert.Equal(sessionId, unpackedSession);
        Assert.NotNull(unpackedPayload);
        Assert.Equal(originalPayload.Length, realLen);

        var resultText = Encoding.UTF8.GetString(unpackedPayload, 0, realLen);
        Assert.Equal("Super secret encrypted VPN packet payload data!", resultText);
    }

    [Fact]
    public void TamperedCiphertextFailsDecryption()
    {
        using var crypto = new AesGcm(_key, 16);
        var sessionId = 0x87654321;

        var payload = "Sensitive VPN packet"u8.ToArray();
        var packed = FechsueCodec.Pack(payload, payload.Length, sessionId, crypto, out var totalLen);

        packed[FechsueCodec.HeaderSize + 2] ^= 0xFF;

        var success = FechsueCodec.TryUnpack(packed, totalLen, crypto, out _, out _, out _);
        Assert.False(success);
    }

    [Fact]
    public void TruncatedPacketFailsUnpack()
    {
        using var crypto = new AesGcm(_key, 16);
        var buffer = new byte[10];
        var success = FechsueCodec.TryUnpack(buffer, buffer.Length, crypto, out _, out _, out _);
        Assert.False(success);
    }
}
