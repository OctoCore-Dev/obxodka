namespace obxodka.Shared.MtuEngine;

public static class Ipv6MtuHandler
{
    //пересчитываем контрольную сумму tcp для ipv6
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecalculateTcpChecksum(Span<byte> pkt)
    {
        var tcpLen = pkt.Length - 40;
        pkt[40 + 16] = 0;
        pkt[40 + 17] = 0;

        uint sum = 0;
        for (var i = 8; i < 40; i += 2)
        {
            sum += (uint)((pkt[i] << 8) | pkt[i + 1]);
        }
        sum += (uint)((tcpLen >> 16) & 0xFFFF);
        sum += (uint)(tcpLen & 0xFFFF);
        sum += 6U;

        var tcpSpan = pkt[40..];
        for (var i = 0; i < tcpLen; i += 2)
        {
            sum += i + 1 < tcpLen
                ? (uint)((tcpSpan[i] << 8) | tcpSpan[i + 1])
                : (uint)(tcpSpan[i] << 8);
        }

        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        BinaryPrimitives.WriteUInt16BigEndian(pkt.Slice(40 + 16, 2), (ushort)~sum);
    }

    //создаем пакет icmpv6 packet too big если размер превышает mtu
    public static bool TryBuildIcmpPacketTooBig(ReadOnlySpan<byte> packet, int length, int clientMtu, out byte[] icmpPacket, out int icmpLen)
    {
        icmpPacket = null!;
        icmpLen = 0;

        if (length < 40 || (packet[0] >> 4) != 6)
        {
            return false;
        }

        var quotedLen = Math.Min(length, 1280 - 48);
        icmpLen = 40 + 8 + quotedLen;
        icmpPacket = ArrayPool<byte>.Shared.Rent(icmpLen);

        icmpPacket[0] = 0x60;
        icmpPacket[1] = 0x00;
        icmpPacket[2] = 0x00;
        icmpPacket[3] = 0x00;

        var payloadLen = 8 + quotedLen;
        BinaryPrimitives.WriteUInt16BigEndian(icmpPacket.AsSpan(4, 2), (ushort)payloadLen);
        icmpPacket[6] = 58;
        icmpPacket[7] = 64;

        packet.Slice(24, 16).CopyTo(icmpPacket.AsSpan(8, 16));
        packet.Slice(8, 16).CopyTo(icmpPacket.AsSpan(24, 16));

        icmpPacket[40] = 2;
        icmpPacket[41] = 0;
        icmpPacket[42] = 0;
        icmpPacket[43] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(icmpPacket.AsSpan(44, 4), (uint)clientMtu);

        packet[..quotedLen].CopyTo(icmpPacket.AsSpan(48, quotedLen));

        uint sum = 0;
        for (var i = 8; i < 40; i += 2)
        {
            sum += (uint)((icmpPacket[i] << 8) | icmpPacket[i + 1]);
        }
        sum += (uint)((payloadLen >> 16) & 0xFFFF);
        sum += (uint)(payloadLen & 0xFFFF);
        sum += 58U;

        var icmp6Span = icmpPacket.AsSpan(40, payloadLen);
        for (var i = 0; i < payloadLen; i += 2)
        {
            sum += i + 1 < payloadLen
                ? (uint)((icmp6Span[i] << 8) | icmp6Span[i + 1])
                : (uint)(icmp6Span[i] << 8);
        }

        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        BinaryPrimitives.WriteUInt16BigEndian(icmpPacket.AsSpan(42, 2), (ushort)~sum);
        return true;
    }
}
