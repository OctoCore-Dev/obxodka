using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace obxodka.Shared.Stealth;

public static class SmartMtuEngine
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ClampTcpMss(Span<byte> pkt, int clientMtu)
    {
        if (pkt.Length < 40)
        {
            return;
        }

        var version = pkt[0] >> 4;
        if (version == 4)
        {
            if (pkt[9] != 6)
            {
                return;
            }

            var ihl = (pkt[0] & 0x0F) * 4;
            if (ihl < 20 || pkt.Length < ihl + 24 || (pkt[ihl + 13] & 0x02) == 0)
            {
                return;
            }

            var tcpHeaderLen = ((pkt[ihl + 12] >> 4) & 0x0F) * 4;
            if (tcpHeaderLen <= 20 || pkt.Length < ihl + tcpHeaderLen)
            {
                return;
            }

            var maxMss = (ushort)Math.Clamp(clientMtu - 40, 536, 1460);
            if (TryPatchTcpOptionsMss(pkt.Slice(ihl + 20, tcpHeaderLen - 20), maxMss))
            {
                RecalculateIpv4TcpChecksum(pkt, ihl);
            }
        }
        else if (version == 6)
        {
            if (pkt.Length < 64 || pkt[6] != 6 || (pkt[40 + 13] & 0x02) == 0)
            {
                return;
            }

            var tcpHeaderLen = ((pkt[40 + 12] >> 4) & 0x0F) * 4;
            if (tcpHeaderLen <= 20 || pkt.Length < 40 + tcpHeaderLen)
            {
                return;
            }

            var maxMss = (ushort)Math.Clamp(clientMtu - 60, 1220, 1440);
            if (TryPatchTcpOptionsMss(pkt.Slice(60, tcpHeaderLen - 20), maxMss))
            {
                RecalculateIpv6TcpChecksum(pkt);
            }
        }
    }

    public static bool TryPatchTcpOptionsMss(Span<byte> options, ushort maxMss)
    {
        var idx = 0;
        while (idx < options.Length)
        {
            var kind = options[idx];
            if (kind == 0)
            {
                break;
            }
            if (kind == 1)
            {
                idx++;
                continue;
            }
            if (idx + 1 >= options.Length)
            {
                break;
            }
            var len = options[idx + 1];
            if (len < 2 || idx + len > options.Length)
            {
                break;
            }
            if (kind == 2 && len == 4)
            {
                var currentMss = BinaryPrimitives.ReadUInt16BigEndian(options.Slice(idx + 2, 2));
                if (currentMss > maxMss)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(options.Slice(idx + 2, 2), maxMss);
                    return true;
                }
                return false;
            }
            idx += len;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecalculateIpv4HeaderChecksum(Span<byte> header)
    {
        header[10] = 0;
        header[11] = 0;
        uint sum = 0;
        for (var i = 0; i < header.Length; i += 2)
        {
            sum += (uint)((header[i] << 8) | header[i + 1]);
        }
        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        BinaryPrimitives.WriteUInt16BigEndian(header.Slice(10, 2), (ushort)~sum);
    }

    public static void RecalculateIpv4TcpChecksum(Span<byte> pkt, int ihl)
    {
        var tcpLen = pkt.Length - ihl;
        pkt[ihl + 16] = 0;
        pkt[ihl + 17] = 0;

        uint sum = 0;
        for (var i = 12; i < 20; i += 2)
        {
            sum += (uint)((pkt[i] << 8) | pkt[i + 1]);
        }
        sum += 6U;
        sum += (uint)tcpLen;

        var tcpSpan = pkt[ihl..];
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

        BinaryPrimitives.WriteUInt16BigEndian(pkt.Slice(ihl + 16, 2), (ushort)~sum);
    }

    public static void RecalculateIpv6TcpChecksum(Span<byte> pkt)
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

    public static bool TryFragmentIpv4(ReadOnlySpan<byte> packet, int length, int clientMtu, Action<byte[], int> onFragmentReady)
    {
        if (length <= clientMtu || length < 20 || (packet[0] >> 4) != 4)
        {
            return false;
        }

        var ihl = (packet[0] & 0x0F) * 4;
        if (ihl < 20 || length < ihl || clientMtu <= ihl)
        {
            return false;
        }

        var maxPayloadPerFrag = (clientMtu - ihl) / 8 * 8;
        if (maxPayloadPerFrag <= 0)
        {
            return false;
        }

        var totalPayload = length - ihl;
        var origFlagsAndOffset = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(6, 2));
        var origMf = (origFlagsAndOffset & 0x2000) != 0;
        var baseFragOffset = (ushort)(origFlagsAndOffset & 0x1FFF);
        var payload = packet.Slice(ihl, totalPayload);

        var offset = 0;
        while (offset < totalPayload)
        {
            var chunk = Math.Min(maxPayloadPerFrag, totalPayload - offset);
            var isLast = offset + chunk >= totalPayload;
            var fragMf = !isLast || origMf;
            var fragOffsetUnits = (ushort)(baseFragOffset + (offset / 8));
            var fragFlagsAndOffset = (ushort)((fragMf ? 0x2000 : 0) | (fragOffsetUnits & 0x1FFF));
            var fragTotalLen = ihl + chunk;

            var fragBuf = ArrayPool<byte>.Shared.Rent(fragTotalLen);
            packet[..ihl].CopyTo(fragBuf);
            BinaryPrimitives.WriteUInt16BigEndian(fragBuf.AsSpan(2, 2), (ushort)fragTotalLen);
            BinaryPrimitives.WriteUInt16BigEndian(fragBuf.AsSpan(6, 2), fragFlagsAndOffset);
            RecalculateIpv4HeaderChecksum(fragBuf.AsSpan(0, ihl));
            payload.Slice(offset, chunk).CopyTo(fragBuf.AsSpan(ihl));

            onFragmentReady(fragBuf, fragTotalLen);
            offset += chunk;
        }

        return true;
    }

    public static bool TryBuildIcmpFragmentationNeeded(ReadOnlySpan<byte> packet, int length, int clientMtu, out byte[] icmpPacket, out int icmpLen)
    {
        icmpPacket = null!;
        icmpLen = 0;

        if (length < 20 || (packet[0] >> 4) != 4)
        {
            return false;
        }

        var ihl = (packet[0] & 0x0F) * 4;
        if (ihl < 20 || length < ihl)
        {
            return false;
        }

        var quotedLen = ihl + Math.Min(8, length - ihl);
        icmpLen = 20 + 8 + quotedLen;
        icmpPacket = ArrayPool<byte>.Shared.Rent(icmpLen);

        icmpPacket[0] = 0x45;
        icmpPacket[1] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(icmpPacket.AsSpan(2, 2), (ushort)icmpLen);
        BinaryPrimitives.WriteUInt16BigEndian(icmpPacket.AsSpan(4, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(icmpPacket.AsSpan(6, 2), 0);
        icmpPacket[8] = 64;
        icmpPacket[9] = 1;

        packet.Slice(16, 4).CopyTo(icmpPacket.AsSpan(12, 4));
        packet.Slice(12, 4).CopyTo(icmpPacket.AsSpan(16, 4));
        RecalculateIpv4HeaderChecksum(icmpPacket.AsSpan(0, 20));

        icmpPacket[20] = 3;
        icmpPacket[21] = 4;
        icmpPacket[22] = 0;
        icmpPacket[23] = 0;
        icmpPacket[24] = 0;
        icmpPacket[25] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(icmpPacket.AsSpan(26, 2), (ushort)clientMtu);

        packet[..quotedLen].CopyTo(icmpPacket.AsSpan(28, quotedLen));

        uint icmpSum = 0;
        var icmpSpan = icmpPacket.AsSpan(20, 8 + quotedLen);
        for (var i = 0; i < icmpSpan.Length; i += 2)
        {
            icmpSum += i + 1 < icmpSpan.Length
                ? (uint)((icmpSpan[i] << 8) | icmpSpan[i + 1])
                : (uint)(icmpSpan[i] << 8);
        }
        while ((icmpSum >> 16) != 0)
        {
            icmpSum = (icmpSum & 0xFFFF) + (icmpSum >> 16);
        }
        BinaryPrimitives.WriteUInt16BigEndian(icmpPacket.AsSpan(22, 2), (ushort)~icmpSum);

        return true;
    }

    public static bool TryBuildIcmpV6PacketTooBig(ReadOnlySpan<byte> packet, int length, int clientMtu, out byte[] icmpPacket, out int icmpLen)
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
