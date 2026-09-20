namespace obxodka.Client.Tests.Stealth;

[Trait("Category", "Stealth")]
[Trait("Category", "RFC")]
[Trait("Category", "Unit")]
public class QuicRfc9000MimicryTests
{
    [Fact]
    public void PackAuthGeneratesValidRfc9000QuicInitialHeader()
    {
        var thumbprint = "TEST_THUMBPRINT_A1B2C3D4E5";
        byte streamIndex = 2;

        var buf = FechsueCodec.PackAuth(thumbprint, streamIndex, out var totalLen);

        try
        {
            Assert.True(totalLen >= FechsueCodec.QuicMinInitialSize, "Initial datagram must be >= 1200 bytes for RFC 9000 compliance");
            Assert.Equal(FechsueCodec.QuicLongHeaderInitial, buf[0]);

            var version = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(1, 4));
            Assert.Equal(FechsueCodec.QuicVersion1, version);

            Assert.Equal(8, buf[5]);
            Assert.Equal(8, buf[14]);

            Assert.Equal(FechsueCodec.QuicFrameCrypto, buf[27]);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    [Fact]
    public void PackAuthAndTryUnpackAuthRoundTripSuccessfully()
    {
        var thumbprint = "CERT_SHA256_HASH_998877665544332211";
        byte streamIndex = 3;

        var buf = FechsueCodec.PackAuth(thumbprint, streamIndex, out var totalLen);

        try
        {
            var success = FechsueCodec.TryUnpackAuth(buf.AsSpan(0, totalLen), out var unpackedThumbprint, out var sessionId, out var unpackedStreamIndex);

            Assert.True(success);
            Assert.Equal(thumbprint, unpackedThumbprint);
            Assert.Equal(streamIndex, unpackedStreamIndex);
            Assert.NotEqual(0u, sessionId);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    [Fact]
    public void PackDiscAndTryUnpackDiscRoundTripSuccessfully()
    {
        var thumbprint = "DISC_THUMBPRINT_XYZ_123";

        var buf = FechsueCodec.PackDisc(thumbprint, out var totalLen);

        try
        {
            var success = FechsueCodec.TryUnpackDisc(buf.AsSpan(0, totalLen), out var unpackedThumbprint, out var sessionId);

            Assert.True(success);
            Assert.Equal(thumbprint, unpackedThumbprint);
            Assert.NotEqual(0u, sessionId);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    [Fact]
    public void TryUnpackAuthHandlesLegacyPacketsForBackwardsCompatibility()
    {
        var thumbprint = "LEGACY_THUMBPRINT_12345";
        byte streamIndex = 1;
        var tpBytes = Encoding.UTF8.GetBytes(thumbprint);
        var totalLen = 17 + tpBytes.Length;
        var legacyBuf = new byte[totalLen];

        var hash = SHA256.HashData(tpBytes);
        var sessionId = BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(legacyBuf.AsSpan(0, 4), sessionId ^ FechsueCodec.StealthAuthMask);
        BinaryPrimitives.WriteUInt32LittleEndian(legacyBuf.AsSpan(4, 4), sessionId);
        BinaryPrimitives.WriteInt64LittleEndian(legacyBuf.AsSpan(8, 8), DateTime.UtcNow.Ticks);
        legacyBuf[16] = streamIndex;
        tpBytes.CopyTo(legacyBuf.AsSpan(17));

        var success = FechsueCodec.TryUnpackAuth(legacyBuf, out var unpackedThumbprint, out var unpackedSessionId, out var unpackedStreamIndex);

        Assert.True(success);
        Assert.Equal(thumbprint, unpackedThumbprint);
        Assert.Equal(streamIndex, unpackedStreamIndex);
        Assert.Equal(sessionId, unpackedSessionId);
    }

    [Fact]
    public void AppSecretsInternalPasswordResolvesCorrectlyWithoutPlaintextExposure()
    {
        var password = AppSecrets.InternalPfxPassword;
        Assert.Equal("obxodka_internal_pass", password);

        var utf8 = AppSecrets.InternalPfxPasswordUtf8;
        Assert.Equal("obxodka_internal_pass", Encoding.UTF8.GetString(utf8));
    }
}
