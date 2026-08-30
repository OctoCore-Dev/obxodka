namespace obxodka.Client.Tests;

[Trait("Category", "Unit")]
public class ObfuscatorTests
{
    [Fact]
    public void PackRecoversHeaderAndData()
    {
        var rawData = "IP Packet raw data 192.168.1.1 -> 1.1.1.1"u8.ToArray();

        var packed = Obfuscator.Pack(rawData, rawData.Length, out var totalLen);
        Assert.NotNull(packed);
        Assert.True(totalLen >= 8 + rawData.Length);

        var extractedTotalLen = BinaryPrimitives.ReadInt32LittleEndian(packed.AsSpan(0, 4));
        var extractedPacketLen = BinaryPrimitives.ReadInt32LittleEndian(packed.AsSpan(4, 4));

        Assert.Equal(totalLen, extractedTotalLen);
        Assert.Equal(rawData.Length, extractedPacketLen);

        var payloadSpan = packed.AsSpan(8, extractedPacketLen);
        Assert.True(payloadSpan.SequenceEqual(rawData));

        ArrayPool<byte>.Shared.Return(packed);
    }

    [Fact]
    public void PackSmartWhenProxiedHasNoPadding()
    {
        var rawData = new byte[100];
        var packed = Obfuscator.PackSmart(rawData, rawData.Length, out var totalLen, isProxied: true);

        Assert.Equal(8 + rawData.Length, totalLen);

        ArrayPool<byte>.Shared.Return(packed);
    }

    [Fact]
    public void GetRaysDistributesAcrossActiveStreams()
    {
        var samplePacket = new byte[40];
        samplePacket[0] = 0x45;
        samplePacket[9] = 6;
        samplePacket[12] = 192;
        samplePacket[13] = 168;
        samplePacket[14] = 1;
        samplePacket[15] = 10;
        samplePacket[16] = 8;
        samplePacket[17] = 8;
        samplePacket[18] = 8;
        samplePacket[19] = 8;
        samplePacket[20] = 0x1F;
        samplePacket[21] = 0x90;
        samplePacket[22] = 0x01;
        samplePacket[23] = 0xBB;

        PacketRouter.GetRays(samplePacket, samplePacket.Length, 4, out var primaryRay, out var secondaryRay);

        Assert.InRange(primaryRay, 0, 3);
        Assert.InRange(secondaryRay, -1, 3);
    }
}
