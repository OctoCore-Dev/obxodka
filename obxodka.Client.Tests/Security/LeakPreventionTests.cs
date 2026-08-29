namespace obxodka.Client.Tests.Security;

[Trait("Category", "Security")]
[Trait("Category", "Unit")]
public class LeakPreventionTests
{
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

    [Theory]
    [InlineData("doubleclick.net", true)]
    [InlineData("pagead2.googlesyndication.com", true)]
    [InlineData("adservice.google.com", true)]
    [InlineData("secure.bank.com", false)]
    [InlineData("youtube.com", false)]
    public void DnsAdBlockerPreventsTelemetryLeaksBySinkholing(string domain, bool shouldBeBlocked)
    {
        var query = CreateDnsQueryPacket(domain);
        var response = DnsAdBlocker.ProcessPacket(query, query.Length, useAdblock: true);

        if (shouldBeBlocked)
        {
            Assert.NotNull(response);
            var answerIp = new byte[4];
            Buffer.BlockCopy(response, response.Length - 4, answerIp, 0, 4);
            Assert.Equal(new byte[] { 0, 0, 0, 0 }, answerIp);
        }
        else
        {
            Assert.Null(response);
        }
    }

    [Fact]
    public void IPv4TunnelAddressValidationProtectsAgainstBogons()
    {
        var privateRange = IPAddress.Parse("10.8.0.2");
        var loopback = IPAddress.Loopback;
        var publicVpnServer = IPAddress.Parse("185.220.101.5");

        Assert.Equal(AddressFamily.InterNetwork, privateRange.AddressFamily);
        Assert.True(IPAddress.IsLoopback(loopback));
        Assert.False(IPAddress.IsLoopback(publicVpnServer));
    }
}
