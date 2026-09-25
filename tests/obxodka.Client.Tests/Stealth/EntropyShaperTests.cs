using System.Security.Cryptography;
using obxodka.Client.Diagnostics;
using obxodka.Shared.Stealth;
using Xunit;

namespace obxodka.Client.Tests.Stealth;

[Trait("Category", "Unit")]
public class EntropyShaperTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(512)]
    [InlineData(1400)]
    public void RoundtripPreservesExactData(int length)
    {
        var original = new byte[length];
        Random.Shared.NextBytes(original);

        var encodedBuf = new byte[EntropyShaper.GetMaxEncodedLength(length)];
        var okEncode = EntropyShaper.TryEncode(original, encodedBuf, out var encodedBytes);
        Assert.True(okEncode);
        Assert.True(encodedBytes > 0 || length == 0);

        var decodedBuf = new byte[length];
        var okDecode = EntropyShaper.TryDecode(encodedBuf.AsSpan(0, encodedBytes), decodedBuf, out var decodedBytes);
        Assert.True(okDecode);
        Assert.Equal(length, decodedBytes);
        Assert.Equal(original, decodedBuf);
    }

    [Fact]
    public void ShaperReducesShannonEntropyFromNearEightToSafeCorridor()
    {
        var rawCrypto = new byte[2048];
        Random.Shared.NextBytes(rawCrypto);

        var rawEntropy = VirtualTspuEngine.CalculateShannonEntropy(rawCrypto);
        Assert.True(rawEntropy >= 7.75);

        var encodedBuf = new byte[EntropyShaper.GetMaxEncodedLength(rawCrypto.Length)];
        var ok = EntropyShaper.TryEncode(rawCrypto, encodedBuf, out var written);
        Assert.True(ok);

        var shapedEntropy = VirtualTspuEngine.CalculateShannonEntropy(encodedBuf.AsSpan(0, written));

        Assert.True(shapedEntropy < 5.50);
        Assert.True(shapedEntropy > 4.50);
    }

    [Fact]
    public void DecodeFailsGracefullyOnTruncatedOrCorruptInput()
    {
        var buf = new byte[10];
        var outBuf = new byte[10];

        Assert.False(EntropyShaper.TryDecode([], outBuf, out _));
        Assert.False(EntropyShaper.TryDecode([0, 0, 0], outBuf, out _));

        buf[0] = 0x10;
        buf[1] = 0x00;
        buf[2] = 0x00;
        buf[3] = 0x00;
        buf[4] = 0xFF;

        Assert.False(EntropyShaper.TryDecode(buf, outBuf, out _));
    }
}
