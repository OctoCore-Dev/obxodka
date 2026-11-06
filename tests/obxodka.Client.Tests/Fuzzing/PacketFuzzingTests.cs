namespace obxodka.Client.Tests.Fuzzing;

[Trait("Category", "Fuzzing")]
[Trait("Category", "Unit")]
public class PacketFuzzingTests
{
    [Fact]
    public void FuzzObxodkaFramingWithRandomSizesNeverCorruptsMemory()
    {
        for (var i = 0; i < 500; i++)
        {
            var packetSize = Random.Shared.Next(1, 1200);
            var sample = new byte[packetSize];
            Random.Shared.NextBytes(sample);

            var packed = ObxodkaFraming.Pack(ObxodkaFraming.CmdData, sample, out var totalLen);
            Assert.NotNull(packed);
            Assert.True(totalLen >= ObxodkaFraming.HeaderSize + sample.Length);

            Assert.True(ObxodkaFraming.TryDecodeHeader(packed.AsSpan(0, ObxodkaFraming.HeaderSize), out var decTotal, out var decPayload, out var cmd, out _));
            Assert.Equal(totalLen, decTotal);
            Assert.Equal(sample.Length, decPayload);
            Assert.Equal(ObxodkaFraming.CmdData, cmd);

            ArrayPool<byte>.Shared.Return(packed);
        }
    }
}
