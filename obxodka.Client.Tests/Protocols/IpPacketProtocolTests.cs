namespace obxodka.Client.Tests.Protocols;

[Trait("Category", "Protocol")]
[Trait("Category", "Unit")]
public class IpPacketProtocolTests
{
    [Fact]
    public void IPv4HeaderVerificationAndTtlInspection()
    {
        var packet = new byte[40];
        packet[0] = 0x45;
        packet[8] = 64;
        packet[9] = 6;

        IPAddress.Parse("10.8.0.2").GetAddressBytes().CopyTo(packet.AsSpan(12, 4));
        IPAddress.Parse("8.8.8.8").GetAddressBytes().CopyTo(packet.AsSpan(16, 4));

        var version = packet[0] >> 4;
        var ihl = (packet[0] & 0x0F) * 4;
        var ttl = packet[8];
        var protocol = packet[9];

        Assert.Equal(4, version);
        Assert.Equal(20, ihl);
        Assert.Equal(64, ttl);
        Assert.Equal(6, protocol);
    }

    [Fact]
    public void UdpDatagramHeaderLengthAndPortParsing()
    {
        var udpHeader = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(udpHeader.AsSpan(0, 2), 53443);
        BinaryPrimitives.WriteUInt16BigEndian(udpHeader.AsSpan(2, 2), 53);
        BinaryPrimitives.WriteUInt16BigEndian(udpHeader.AsSpan(4, 2), 512);
        BinaryPrimitives.WriteUInt16BigEndian(udpHeader.AsSpan(6, 2), 0);

        var srcPort = BinaryPrimitives.ReadUInt16BigEndian(udpHeader.AsSpan(0, 2));
        var dstPort = BinaryPrimitives.ReadUInt16BigEndian(udpHeader.AsSpan(2, 2));
        var length = BinaryPrimitives.ReadUInt16BigEndian(udpHeader.AsSpan(4, 2));

        Assert.Equal(53443, srcPort);
        Assert.Equal(53, dstPort);
        Assert.Equal(512, length);
    }

    [Fact]
    public void TcpFlagsExtractionSynAckFinPsh()
    {
        byte synAck = 0x12;
        var isSyn = (synAck & 0x02) != 0;
        var isAck = (synAck & 0x10) != 0;
        var isFin = (synAck & 0x01) != 0;

        Assert.True(isSyn);
        Assert.True(isAck);
        Assert.False(isFin);
    }
}
