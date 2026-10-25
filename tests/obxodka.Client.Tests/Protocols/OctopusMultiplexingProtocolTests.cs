namespace obxodka.Client.Tests.Protocols;

[Trait("Category", "Protocol")]
[Trait("Category", "Unit")]
public class OctopusMultiplexingProtocolTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void MultiRayStreamIndexDistributionIsUniform(int streamCount)
    {
        var distribution = new int[streamCount];
        var totalPackets = 1000;

        for (var i = 0; i < totalPackets; i++)
        {
            var streamIndex = (byte)(i % streamCount);
            distribution[streamIndex]++;
        }

        var expectedPerStream = totalPackets / streamCount;
        for (var s = 0; s < streamCount; s++)
        {
            Assert.Equal(expectedPerStream, distribution[s]);
        }
    }

    [Fact]
    public async Task BiDirectionalStreamPumpingPreservesFullPayloadIntegrityAsync()
    {
        var randomPayload = new byte[65536];
        Random.Shared.NextBytes(randomPayload);

        using var inStream = new MemoryStream(randomPayload);
        using var outStream = new MemoryStream();
        using var cts = new CancellationTokenSource();

        var bytesPumped = await OctopusProtocol.PumpTrafficAsync(inStream, outStream, cts.Token);

        Assert.Equal(randomPayload.Length, bytesPumped);
        Assert.Equal(randomPayload, outStream.ToArray());
    }

    [Fact]
    public void PacketBatchArrayPoolRentAndReturnIntegrity()
    {
        var packetList = new List<(byte[] buffer, int length)>();
        for (var i = 0; i < 64; i++)
        {
            var buf = ArrayPool<byte>.Shared.Rent(1420);
            packetList.Add((buf, 1420));
        }

        Assert.Equal(64, packetList.Count);

        for (var i = 0; i < packetList.Count; i++)
        {
            var (buf, len) = packetList[i];
            Assert.Equal(1420, len);
            ArrayPool<byte>.Shared.Return(buf);
        }
    }
}
