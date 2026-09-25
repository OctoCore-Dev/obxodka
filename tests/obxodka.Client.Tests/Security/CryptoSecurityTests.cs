namespace obxodka.Client.Tests;

[Trait("Category", "Unit")]
public class CryptoSecurityTests
{
    private readonly byte[] _masterKey = SHA256.HashData("SecurityTestKeyMasterSecret_2026"u8.ToArray());

    [Fact]
    public void ParallelEncryptionDecryptionUnderLoadThreadSafe()
    {
        var masterKey = _masterKey;

        _ = Parallel.For(0, 50, i =>
        {
            using var crypto = new AesGcm(masterKey, 16);
            var sessionId = (uint)(1000 + i);

            var testPayload = Encoding.UTF8.GetBytes($"Concurrent packet payload index={i} timestamp={DateTime.UtcNow.Ticks}");
            var packed = FechsueCodec.Pack(testPayload, testPayload.Length, sessionId, crypto, out var totalLen);

            Assert.NotNull(packed);
            Assert.True(totalLen >= testPayload.Length + FechsueCodec.Overhead);

            var success = FechsueCodec.TryUnpack(packed, totalLen, crypto, out var extractedSession, out var unpacked, out var outLen);
            Assert.True(success);
            Assert.NotNull(unpacked);
            Assert.Equal(sessionId, extractedSession);
            Assert.Equal(testPayload.Length, outLen);
            Assert.True(unpacked.AsSpan(0, outLen).SequenceEqual(testPayload));

            ArrayPool<byte>.Shared.Return(packed);
            ArrayPool<byte>.Shared.Return(unpacked);
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(512)]
    [InlineData(1420)]
    public void VariousPacketSizesRoundTripCleanly(int payloadSize)
    {
        using var crypto = new AesGcm(_masterKey, 16);
        var sessionId = 0xDEADBEEF;

        var randomPayload = new byte[payloadSize];
        Random.Shared.NextBytes(randomPayload);

        var packed = FechsueCodec.Pack(randomPayload, randomPayload.Length, sessionId, crypto, out var totalLen);
        var success = FechsueCodec.TryUnpack(packed, totalLen, crypto, out var extractedSession, out var unpacked, out var outLen);

        Assert.True(success);
        Assert.NotNull(unpacked);
        Assert.Equal(sessionId, extractedSession);
        Assert.Equal(payloadSize, outLen);
        Assert.True(unpacked.AsSpan(0, outLen).SequenceEqual(randomPayload));

        ArrayPool<byte>.Shared.Return(packed);
        ArrayPool<byte>.Shared.Return(unpacked);
    }

    [Fact]
    public void CorruptedLengthHeaderFailsDecryptionSafely()
    {
        using var crypto = new AesGcm(_masterKey, 16);
        var payload = "Sensitive network payload"u8.ToArray();
        var packed = FechsueCodec.Pack(payload, payload.Length, 12345, crypto, out var totalLen);

        var success = FechsueCodec.TryUnpack(packed, FechsueCodec.Overhead - 1, crypto, out _, out var unpacked, out _);
        Assert.False(success);
        Assert.Null(unpacked);

        ArrayPool<byte>.Shared.Return(packed);
    }
}
