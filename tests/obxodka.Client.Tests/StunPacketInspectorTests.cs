using obxodka.Core.Pipeline;

namespace obxodka.Client.Tests;

//тесты детектора webrtc stun/turn пакетов и доменов для защиты от утечки ip при обходе рф
public sealed class StunPacketInspectorTests
{
    [Theory]
    [InlineData(3478)]
    [InlineData(5349)]
    [InlineData(19302)]
    public void IsStunIpv4Packet_DetectsStandardStunPorts(ushort dstPort)
    {
        var pkt = BuildIpv4UdpPacket(51000, dstPort, [0x01, 0x02, 0x03, 0x04]);

        Assert.True(StunPacketInspector.IsStunIpv4Packet(pkt));
        Assert.True(PacketPipeline.IsStunPacket(pkt));
    }

    [Fact]
    public void IsStunIpv4Packet_DetectsRfc5389MagicCookieOnNonStandardPort()
    {
        //rfc 5389 binding request: type 0x0001, length 0x0000, magic cookie 0x2112A442 + 12 байт transaction id
        var stunPayload = new byte[20];
        stunPayload[0] = 0x00;
        stunPayload[1] = 0x01;
        stunPayload[4] = 0x21;
        stunPayload[5] = 0x12;
        stunPayload[6] = 0xA4;
        stunPayload[7] = 0x42;

        var pkt = BuildIpv4UdpPacket(52123, 49152, stunPayload);

        Assert.True(StunPacketInspector.IsStunIpv4Packet(pkt));
        Assert.True(PacketPipeline.IsStunPacket(pkt));
    }

    [Fact]
    public void IsStunIpv4Packet_IgnoresRegularQuicOrDnsUdpPackets()
    {
        //обычный quic пакет (старшие 2 бита = 11, порт 443) не должен считаться stun
        var quicPayload = new byte[32];
        quicPayload[0] = 0xC0;
        quicPayload[4] = 0x21;
        quicPayload[5] = 0x12;
        quicPayload[6] = 0xA4;
        quicPayload[7] = 0x42;

        var quicPkt = BuildIpv4UdpPacket(52123, 443, quicPayload);
        Assert.False(StunPacketInspector.IsStunIpv4Packet(quicPkt));

        var dnsPkt = BuildIpv4UdpPacket(52123, 53, new byte[24]);
        Assert.False(StunPacketInspector.IsStunIpv4Packet(dnsPkt));
    }

    [Fact]
    public void IsStunIpv6Packet_DetectsMagicCookieAndStandardPorts()
    {
        var stunPayload = new byte[20];
        stunPayload[0] = 0x00;
        stunPayload[1] = 0x01;
        stunPayload[4] = 0x21;
        stunPayload[5] = 0x12;
        stunPayload[6] = 0xA4;
        stunPayload[7] = 0x42;

        var pkt = new byte[48 + stunPayload.Length];
        pkt[0] = 0x60; //ipv6
        pkt[6] = 17;   //udp
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(40, 2), 54321);
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(42, 2), 50000);
        stunPayload.CopyTo(pkt, 48);

        Assert.True(StunPacketInspector.IsStunIpv6Packet(pkt));
        Assert.True(PacketPipeline.IsStunPacket(pkt));
    }

    [Theory]
    [InlineData("stun.yandex.ru", true)]
    [InlineData("stun1.l.google.com", true)]
    [InlineData("turn.vk.com", true)]
    [InlineData("webrtc-stun.mail.ru", true)]
    [InlineData("media.turn02.example.ru", true)]
    [InlineData("yandex.ru", false)]
    [InlineData("saturn.ru", false)]
    [InlineData("return.ozon.ru", false)]
    [InlineData("vk.com", false)]
    public void IsStunHostname_DistinguishesStunFromOrdinaryRussianDomains(string host, bool expected)
    {
        Assert.Equal(expected, StunPacketInspector.IsStunHostname(host));
    }

    [Fact]
    public void SplitTunnelPolicy_NeverAutoBypassesRussianStunServers()
    {
        var policy = new SplitTunnelPolicy
        {
            Enabled = true,
            BypassAllRuTlds = true,
            BypassRuMedia = true
        };

        Assert.False(policy.IsDomainBypassed("stun.yandex.ru"));
        Assert.False(policy.IsDomainBypassed("turn.vk.com"));
        Assert.True(policy.IsDomainBypassed("yandex.ru"));
        Assert.True(policy.IsDomainBypassed("saturn.ru"));
    }

    private static byte[] BuildIpv4UdpPacket(ushort srcPort, ushort dstPort, byte[] udpPayload)
    {
        var pkt = new byte[20 + 8 + udpPayload.Length];
        pkt[0] = 0x45; //ipv4, ihl=20
        pkt[9] = 17;   //udp
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(2, 2), (ushort)pkt.Length);
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(20, 2), srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(22, 2), dstPort);
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(24, 2), (ushort)(8 + udpPayload.Length));
        udpPayload.CopyTo(pkt, 28);
        return pkt;
    }
}
