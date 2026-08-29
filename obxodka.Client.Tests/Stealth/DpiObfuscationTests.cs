namespace obxodka.Client.Tests.Stealth;

[Trait("Category", "Stealth")]
[Trait("Category", "Unit")]
public class DpiObfuscationTests
{
    private static double CalculateShannonEntropy(byte[] data, int length)
    {
        if (length == 0)
        {
            return 0;
        }

        var counts = new int[256];
        for (var i = 0; i < length; i++)
        {
            counts[data[i]]++;
        }

        double entropy = 0;
        for (var i = 0; i < 256; i++)
        {
            if (counts[i] > 0)
            {
                var p = (double)counts[i] / length;
                entropy -= p * Math.Log2(p);
            }
        }
        return entropy;
    }

    [Fact]
    public void EncryptedVpnPacketsHaveHighShannonEntropyLikeTls13()
    {
        var key = SHA256.HashData("HighEntropySecret2026"u8.ToArray());
        using var crypto = new AesGcm(key, 16);

        var repetitivePayload = new byte[1024];
        Array.Fill<byte>(repetitivePayload, 0x41);

        var rawEntropy = CalculateShannonEntropy(repetitivePayload, repetitivePayload.Length);
        Assert.True(rawEntropy < 0.1);

        var packed = FechsueCodec.Pack(repetitivePayload, repetitivePayload.Length, 1234, crypto, out var totalLen);

        var encryptedEntropy = CalculateShannonEntropy(packed, totalLen);
        ArrayPool<byte>.Shared.Return(packed);

        Assert.True(encryptedEntropy > 7.0, $"Entropy was {encryptedEntropy}, expected > 7.0 for stealth");
    }

    [Fact]
    public void DpiBypassStreamSplitsSniWithoutCorruptingHttpStream()
    {
        using var outputMemory = new MemoryStream();
        using var bypassStream = new DpiBypassStream(outputMemory);

        var sampleHttpRequest = "GET /index.html HTTP/1.1\r\nHost: blocked-service.com\r\nUser-Agent: ObxodkaClient\r\n\r\n"u8.ToArray();

        bypassStream.Write(sampleHttpRequest, 0, sampleHttpRequest.Length);
        bypassStream.Flush();

        var transmitted = outputMemory.ToArray();
        Assert.Equal(sampleHttpRequest, transmitted);
    }
}
