namespace obxodka.Client.Tests.Fuzzing;

[Trait("Category", "Fuzzing")]
[Trait("Category", "Unit")]
public class PacketFuzzingTests
{
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
}
