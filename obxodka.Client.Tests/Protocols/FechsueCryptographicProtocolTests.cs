namespace obxodka.Client.Tests.Protocols;

[Trait("Category", "Protocol")]
[Trait("Category", "Security")]
public class FechsueCryptographicProtocolTests
{
    private readonly byte[] _key = SHA256.HashData("FechsueProtocolContractSecretKey2026"u8.ToArray());

    [Fact]
    public void FechsueAuthMagicHeaderProtocolContract()
    {
        Assert.Equal(4, FechsueCodec.AuthMagic.Length);
        Assert.Equal((byte)'F', FechsueCodec.AuthMagic[0]);
        Assert.Equal((byte)'E', FechsueCodec.AuthMagic[1]);
        Assert.Equal((byte)'C', FechsueCodec.AuthMagic[2]);
        Assert.Equal((byte)'H', FechsueCodec.AuthMagic[3]);
    }

    [Fact]
    public void FechsuePacketOverheadIsExactly32Bytes()
    {
        Assert.Equal(16, FechsueCodec.HeaderSize);
        Assert.Equal(16, FechsueCodec.TagSize);
        Assert.Equal(32, FechsueCodec.Overhead);
    }

    [Fact]
    public void MonotonicallyIncreasingNonceCounterPreventsNonceReuse()
    {
        using var crypto = new AesGcm(_key, 16);
        var payload = "TestNonce"u8.ToArray();

        var packet1 = FechsueCodec.Pack(payload, payload.Length, 1, crypto, out var len1);
        var packet2 = FechsueCodec.Pack(payload, payload.Length, 1, crypto, out var len2);

        var nonce1 = packet1.AsSpan(0, 12).ToArray();
        var nonce2 = packet2.AsSpan(0, 12).ToArray();

        Assert.False(nonce1.AsSpan().SequenceEqual(nonce2));

        ArrayPool<byte>.Shared.Return(packet1);
        ArrayPool<byte>.Shared.Return(packet2);
    }

    [Fact]
    public void SessionIdIsolationRejectsForeignSessionKeys()
    {
        var key1 = SHA256.HashData("SessionA"u8.ToArray());
        var key2 = SHA256.HashData("SessionB"u8.ToArray());

        using var cryptoA = new AesGcm(key1, 16);
        using var cryptoB = new AesGcm(key2, 16);

        var payload = "SecretSessionData"u8.ToArray();
        var packetA = FechsueCodec.Pack(payload, payload.Length, 101, cryptoA, out var lenA);

        var success = FechsueCodec.TryUnpack(packetA, lenA, cryptoB, out _, out var unp, out _);

        Assert.False(success);
        Assert.Null(unp);

        ArrayPool<byte>.Shared.Return(packetA);
    }
}
