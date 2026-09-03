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

    [Fact]
    public void TryUnpackExtractsValidPayload()
    {
        var rawData = "Octopus Test Payload"u8.ToArray();
        var packed = Obfuscator.Pack(rawData, rawData.Length, out var totalLen);

        var success = Obfuscator.TryUnpack(packed.AsSpan(0, totalLen), out var realLen, out var payload);
        Assert.True(success);
        Assert.Equal(rawData.Length, realLen);
        Assert.True(payload.SequenceEqual(rawData));

        ArrayPool<byte>.Shared.Return(packed);
    }

    [Fact]
    public async Task ReadPacketAsyncCorrectlyReadsStreamAsync()
    {
        var rawData = "Async stream packet payload"u8.ToArray();
        var packed = Obfuscator.Pack(rawData, rawData.Length, out var totalLen);

        using var ms = new MemoryStream(packed, 0, totalLen);
        var header = new byte[8];
        var (packet, len) = await Obfuscator.ReadPacketAsync(ms, header, CancellationToken.None);

        Assert.NotNull(packet);
        Assert.Equal(rawData.Length, len);
        Assert.True(packet.AsSpan(0, len).SequenceEqual(rawData));

        ArrayPool<byte>.Shared.Return(packet);
        ArrayPool<byte>.Shared.Return(packed);
    }

    [Fact]
    public void ClientHelloBuilderGeneratesValidTlsRecord()
    {
        var sni = "example.com"u8;
        var hello = ClientHelloBuilder.BuildChrome120ClientHello(sni);

        Assert.NotNull(hello);
        Assert.True(hello.Length > 100);
        Assert.Equal(0x16, hello[0]);
        Assert.Equal(0x03, hello[1]);
        Assert.Equal(0x01, hello[2]);
        var recordLen = BinaryPrimitives.ReadUInt16BigEndian(hello.AsSpan(3, 2));
        Assert.Equal(hello.Length - 5, recordLen);
        Assert.Equal(0x01, hello[5]);
    }

    [Fact]
    public void QuicInitialBuilderGeneratesValidRfc9000Header()
    {
        var dcid = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var scid = new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 };
        var clientHello = "DummyClientHello"u8.ToArray();

        var packet = QuicInitialBuilder.BuildInitialPacket(dcid, scid, clientHello);

        Assert.NotNull(packet);
        Assert.True(packet.Length > dcid.Length + scid.Length + clientHello.Length);
        var headerByte = packet[0];
        Assert.Equal(0xC0, headerByte & 0xFC);
    }
}
