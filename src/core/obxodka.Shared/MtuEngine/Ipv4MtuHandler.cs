using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace obxodka.Shared.MtuEngine;

public static class Ipv4MtuHandler
{
    private static int s_fragIdSequence = RandomNumberGenerator.GetInt32(1, 65535);

    //математически перечитываем и уменьшаем контрольную сумму заголовка IPv4, чтобы она соответствовала измененному внешнему заголовку
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecalculateHeaderChecksum(Span<byte> header)
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

    //математически перечитываем и уменьшаем контрольную сумму заголовка IPv4, чтобы она соответствовала измененному внутреннему заголовку
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecalculateTcpChecksum(Span<byte> pkt, int ihl)
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

    //фрагментируем пакет IPv4 на несколько фрагментов, если он превышает MTU клиента, и вызываем обратный вызов для каждого готового фрагмента
    public static bool TryFragment(ReadOnlySpan<byte> packet, int length, int clientMtu, Action<byte[], int> onFragmentReady)
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

        //если исходный идентификатор равен 0 (типично для пакетов с DF=1), генерируем уникальный ненулевой ID для корректной сборки стеком ОС
        var origId = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(4, 2));
        var fragId = origId != 0 ? origId : (ushort)(Interlocked.Increment(ref s_fragIdSequence) & 0xFFFF);
        if (fragId == 0)
        {
            fragId = (ushort)(Interlocked.Increment(ref s_fragIdSequence) & 0xFFFF);
        }

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
            BinaryPrimitives.WriteUInt16BigEndian(fragBuf.AsSpan(4, 2), fragId);
            BinaryPrimitives.WriteUInt16BigEndian(fragBuf.AsSpan(6, 2), fragFlagsAndOffset);
            RecalculateHeaderChecksum(fragBuf.AsSpan(0, ihl));
            payload.Slice(offset, chunk).CopyTo(fragBuf.AsSpan(ihl));

            onFragmentReady(fragBuf, fragTotalLen);
            offset += chunk;
        }

        return true;
    }

    //механизм PMTUD: создаем ICMP-пакет "Fragmentation Needed" для уведомления отправителя о том, что пакет слишком велик для передачи без фрагментации
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
        RecalculateHeaderChecksum(icmpPacket.AsSpan(0, 20));

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
}
