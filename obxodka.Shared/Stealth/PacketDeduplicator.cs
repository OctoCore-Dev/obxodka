namespace obxodka.Shared.Stealth;

public sealed class PacketDeduplicator
{
    private const int RingSize = 8192;
    private const int RingMask = RingSize - 1;
    private readonly long[] _seenKeys = new long[RingSize];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsDuplicate(byte[] packet, int length)
    {
        if (length < 20)
        {
            return false;
        }

        var version = packet[0] >> 4;
        long key;

        if (version == 4)
        {
            var id = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4, 2));
            var protocol = packet[9];
            var srcIp = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(12, 4));
            var dstIp = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(16, 4));

            var ihl = (packet[0] & 0x0F) * 4;
            var srcPort = length >= ihl + 2 ? BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(ihl, 2)) : 0;
            var dstPort = length >= ihl + 4 ? BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(ihl + 2, 2)) : 0;

            key = ((long)id << 48) ^ ((long)protocol << 40) ^ ((long)srcPort << 24) ^ ((long)dstPort << 8) ^ (srcIp ^ dstIp);
        }
        else if (version == 6 && length >= 40)
        {
            var nextHeader = packet[6];
            var srcIpHash = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(20, 4));
            var dstIpHash = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(36, 4));
            var srcPort = length >= 42 ? BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(40, 2)) : 0;
            var dstPort = length >= 44 ? BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(42, 2)) : 0;

            key = ((long)nextHeader << 48) ^ ((long)srcPort << 32) ^ ((long)dstPort << 16) ^ (srcIpHash ^ dstIpHash);
        }
        else
        {
            return false;
        }

        var slot = (int)((ulong)key & RingMask);
        var prev = Interlocked.Exchange(ref _seenKeys[slot], key);
        return prev == key;
    }
}
