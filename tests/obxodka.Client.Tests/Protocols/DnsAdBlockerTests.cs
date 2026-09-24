namespace obxodka.Client.Tests;

[Trait("Category", "Unit")]
public class DnsAdBlockerTests
{
    [Fact]
    public void ProcessPacketWhenAdblockDisabledReturnsNull()
    {
        var packet = new byte[100];
        var result = DnsAdBlocker.ProcessPacket(packet, packet.Length, false);
        Assert.Null(result);
    }

    [Fact]
    public void ProcessPacketWhenQueryingBlockedDomainReturnsZeroIpResponse()
    {
        var packet = CreateDnsQueryPacket("googleadservices.com");
        var result = DnsAdBlocker.ProcessPacket(packet, packet.Length, true);

        Assert.NotNull(result);
        var answerIp = new byte[4];
        Buffer.BlockCopy(result, result.Length - 4, answerIp, 0, 4);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, answerIp);
    }

    [Fact]
    public void ProcessPacketWhenQueryingCleanDomainReturnsNull()
    {
        var packet = CreateDnsQueryPacket("example.com");
        var result = DnsAdBlocker.ProcessPacket(packet, packet.Length, true);
        Assert.Null(result);
    }

    private static byte[] CreateDnsQueryPacket(string domain)
    {
        var packet = new byte[20 + 8 + 12 + domain.Length + 2 + 4];
        packet[0] = 0x45;
        packet[9] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20, 2), 12345);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), 53);
        var dnsOffset = 28;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(dnsOffset + 4, 2), 1);
        var qnameOffset = dnsOffset + 12;
        var parts = domain.Split('.');
        var currentOffset = qnameOffset;
        foreach (var part in parts)
        {
            packet[currentOffset++] = (byte)part.Length;
            var partBytes = Encoding.UTF8.GetBytes(part);
            Buffer.BlockCopy(partBytes, 0, packet, currentOffset, partBytes.Length);
            currentOffset += partBytes.Length;
        }
        packet[currentOffset++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(currentOffset, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(currentOffset + 2, 2), 1);

        return packet;
    }
}
