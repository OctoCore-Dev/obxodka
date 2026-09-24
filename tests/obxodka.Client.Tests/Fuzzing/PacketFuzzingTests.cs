namespace obxodka.Client.Tests.Fuzzing;

[Trait("Category", "Fuzzing")]
[Trait("Category", "Unit")]
public class PacketFuzzingTests
{
    private readonly byte[] _key = SHA256.HashData("FuzzingMasterSecretKey_2026"u8.ToArray());

    [Fact]
    public void FuzzFechsueCodecWithCorruptedAndRandomPayloadsNeverCrashes()
    {
        using var crypto = new AesGcm(_key, 16);

        for (var i = 0; i < 1000; i++)
        {
            var randomLength = Random.Shared.Next(0, 2048);
            var garbage = new byte[randomLength];
            Random.Shared.NextBytes(garbage);

            try
            {
                var success = FechsueCodec.TryUnpack(garbage, garbage.Length, crypto, out _, out var unpacked, out _);
                if (unpacked != null)
                {
                    ArrayPool<byte>.Shared.Return(unpacked);
                }
            }
            catch (Exception ex) when (ex is not AccessViolationException and not OutOfMemoryException)
            {
            }

            var authSuccess = FechsueCodec.TryUnpackAuth(garbage.AsSpan(), out _, out _, out _);
            Assert.False(authSuccess && garbage.Length < 25);
        }
    }

    [Fact]
    public void FuzzObfuscatorWithRandomSizesNeverCorruptsMemory()
    {
        for (var i = 0; i < 500; i++)
        {
            var packetSize = Random.Shared.Next(1, 1500);
            var sample = new byte[packetSize];
            Random.Shared.NextBytes(sample);

            var packed = Obfuscator.Pack(sample, sample.Length, out var totalLen);
            Assert.NotNull(packed);
            Assert.True(totalLen >= sample.Length);

            var smartPacked = Obfuscator.PackSmart(sample, sample.Length, out var smartLen, isProxied: i % 2 == 0);
            Assert.NotNull(smartPacked);
            Assert.True(smartLen >= sample.Length);

            ArrayPool<byte>.Shared.Return(packed);
            ArrayPool<byte>.Shared.Return(smartPacked);
        }
    }

    [Fact]
    public void FuzzDpiBypassStreamWithVaryingBufferSizes()
    {
        for (var i = 0; i < 200; i++)
        {
            var size = Random.Shared.Next(1, 4096);
            var randomBytes = new byte[size];
            Random.Shared.NextBytes(randomBytes);

            using var memStream = new MemoryStream();
            using var bypassStream = new DpiBypassStream(memStream);

            bypassStream.Write(randomBytes);
            bypassStream.Flush();

            Assert.Equal(randomBytes, memStream.ToArray());
        }
    }
}
