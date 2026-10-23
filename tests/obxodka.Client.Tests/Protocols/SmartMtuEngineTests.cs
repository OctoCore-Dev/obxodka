using System.Buffers.Binary;
using obxodka.Shared.Stealth;
using Xunit;

namespace obxodka.Client.Tests.Protocols;

[Trait("Category", "Unit")]
public sealed class SmartMtuEngineTests
{
    [Fact]
    public void TryFragmentIpv4LargePacketFragmentsCorrectly()
    {
        var originalTotalLen = 1500;
        var ihl = 20;
        var originalPacket = new byte[originalTotalLen];
        originalPacket[0] = 0x45;
        originalPacket[1] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(originalPacket.AsSpan(2, 2), (ushort)originalTotalLen);
        BinaryPrimitives.WriteUInt16BigEndian(originalPacket.AsSpan(4, 2), 0xBEEF);
        BinaryPrimitives.WriteUInt16BigEndian(originalPacket.AsSpan(6, 2), 0x0000);
        originalPacket[8] = 64;
        originalPacket[9] = 17;
        originalPacket[12] = 100;
        originalPacket[13] = 64;
        originalPacket[14] = 0;
        originalPacket[15] = 2;
        originalPacket[16] = 1;
        originalPacket[17] = 1;
        originalPacket[18] = 1;
        originalPacket[19] = 1;

        for (var i = ihl; i < originalTotalLen; i++)
        {
            originalPacket[i] = (byte)(i % 251);
        }

        SmartMtuEngine.RecalculateIpv4HeaderChecksum(originalPacket.AsSpan(0, ihl));

        var clientMtu = 1280;
        var fragments = new List<byte[]>();

        var fragmented = SmartMtuEngine.TryFragmentIpv4(originalPacket, originalTotalLen, clientMtu, (buf, len) =>
        {
            var copy = new byte[len];
            Buffer.BlockCopy(buf, 0, copy, 0, len);
            fragments.Add(copy);
        });

        Assert.True(fragmented);
        Assert.Equal(2, fragments.Count);

        var frag1 = fragments[0];
        var frag2 = fragments[1];

        Assert.True(frag1.Length <= clientMtu);
        Assert.True(frag2.Length <= clientMtu);

        var frag1FlagsAndOffset = BinaryPrimitives.ReadUInt16BigEndian(frag1.AsSpan(6, 2));
        var frag1Mf = (frag1FlagsAndOffset & 0x2000) != 0;
        var frag1Offset = frag1FlagsAndOffset & 0x1FFF;

        Assert.True(frag1Mf);
        Assert.Equal(0, frag1Offset);

        var frag2FlagsAndOffset = BinaryPrimitives.ReadUInt16BigEndian(frag2.AsSpan(6, 2));
        var frag2Mf = (frag2FlagsAndOffset & 0x2000) != 0;
        var frag2Offset = frag2FlagsAndOffset & 0x1FFF;

        Assert.False(frag2Mf);
        Assert.True(frag2Offset > 0);

        var frag1PayloadLen = frag1.Length - ihl;
        var frag2PayloadLen = frag2.Length - ihl;
        Assert.Equal(originalTotalLen - ihl, frag1PayloadLen + frag2PayloadLen);

        var reassembled = new byte[originalTotalLen - ihl];
        Buffer.BlockCopy(frag1, ihl, reassembled, 0, frag1PayloadLen);
        Buffer.BlockCopy(frag2, ihl, reassembled, frag1PayloadLen, frag2PayloadLen);

        Assert.True(originalPacket.AsSpan(ihl).SequenceEqual(reassembled));
    }

    [Fact]
    public void TryFragmentIpv4SmallPacketReturnsFalse()
    {
        var pkt = new byte[500];
        pkt[0] = 0x45;
        var fragmented = SmartMtuEngine.TryFragmentIpv4(pkt, 500, 1280, (_, _) => { });
        Assert.False(fragmented);
    }

    [Fact]
    public void TryBuildIcmpFragmentationNeededValidIpv4CreatesCorrectIcmpPacket()
    {
        var originalPacket = new byte[1400];
        originalPacket[0] = 0x45;
        originalPacket[8] = 64;
        originalPacket[9] = 6;
        originalPacket[12] = 192;
        originalPacket[13] = 0;
        originalPacket[14] = 2;
        originalPacket[15] = 1;
        originalPacket[16] = 198;
        originalPacket[17] = 51;
        originalPacket[18] = 100;
        originalPacket[19] = 1;

        var clientMtu = 1360;
        var built = SmartMtuEngine.TryBuildIcmpFragmentationNeeded(originalPacket, 1400, clientMtu, out var icmpPkt, out var icmpLen);

        Assert.True(built);
        Assert.NotNull(icmpPkt);
        Assert.True(icmpLen >= 28);
        Assert.Equal(0x45, icmpPkt[0]);
        Assert.Equal(1, icmpPkt[9]);

        Assert.Equal(3, icmpPkt[20]);
        Assert.Equal(4, icmpPkt[21]);

        var reportedMtu = BinaryPrimitives.ReadUInt16BigEndian(icmpPkt.AsSpan(26, 2));
        Assert.Equal(clientMtu, reportedMtu);

        System.Buffers.ArrayPool<byte>.Shared.Return(icmpPkt);
    }

    [Fact]
    public void TryBuildIcmpV6PacketTooBigValidIpv6CreatesCorrectIcmp6Packet()
    {
        var originalPacket = new byte[1440];
        originalPacket[0] = 0x60;
        originalPacket[6] = 6;

        for (var i = 8; i < 24; i++)
        {
            originalPacket[i] = (byte)(i + 1);
        }
        for (var i = 24; i < 40; i++)
        {
            originalPacket[i] = (byte)(i + 2);
        }

        var clientMtu = 1300;
        var built = SmartMtuEngine.TryBuildIcmpV6PacketTooBig(originalPacket, 1440, clientMtu, out var icmpPkt, out var icmpLen);

        Assert.True(built);
        Assert.NotNull(icmpPkt);
        Assert.True(icmpLen >= 48);
        Assert.Equal(0x60, icmpPkt[0]);
        Assert.Equal(58, icmpPkt[6]);

        Assert.Equal(2, icmpPkt[40]);
        Assert.Equal(0, icmpPkt[41]);

        var reportedMtu = BinaryPrimitives.ReadUInt32BigEndian(icmpPkt.AsSpan(44, 4));
        Assert.Equal((uint)clientMtu, reportedMtu);

        System.Buffers.ArrayPool<byte>.Shared.Return(icmpPkt);
    }

    [Fact]
    public void ClampTcpMssIpv4SynClampsToClientMtu()
    {
        var pkt = new byte[60];
        pkt[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(2, 2), 60);
        pkt[9] = 6;
        pkt[12] = 100;
        pkt[13] = 64;
        pkt[14] = 0;
        pkt[15] = 5;
        pkt[16] = 1;
        pkt[17] = 1;
        pkt[18] = 1;
        pkt[19] = 1;

        pkt[32] = 0xA0;
        pkt[33] = 0x02;

        pkt[40] = 2;
        pkt[41] = 4;
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(42, 2), 1460);

        SmartMtuEngine.ClampTcpMss(pkt.AsSpan(0, 60), 1280);

        var clampedMss = BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(42, 2));
        Assert.Equal(1240, clampedMss);
    }

    [Fact]
    public void ClampTcpMssCustomRegionalMtuAdaptsCorrectly()
    {
        var pkt = new byte[60];
        pkt[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(2, 2), 60);
        pkt[9] = 6;
        pkt[12] = 100;
        pkt[13] = 64;
        pkt[14] = 0;
        pkt[15] = 5;
        pkt[16] = 1;
        pkt[17] = 1;
        pkt[18] = 1;
        pkt[19] = 1;

        pkt[32] = 0xA0;
        pkt[33] = 0x02;

        pkt[40] = 2;
        pkt[41] = 4;
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(42, 2), 1460);

        SmartMtuEngine.ClampTcpMss(pkt.AsSpan(0, 60), 1400);

        var clampedMss = BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(42, 2));
        Assert.Equal(1360, clampedMss);
    }
}
