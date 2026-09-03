namespace obxodka.Stealth;

public sealed class PacketDeduplicator
{
    private const int RingSize = 16384;
    private const int RingMask = RingSize - 1;
    private const long MaxDuplicateAgeMs = 500; // 500ms TTL

    private readonly ulong[] _seenKeys = new ulong[RingSize];
    private readonly long[] _seenTimestamps = new long[RingSize];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsDuplicate(byte[] packet, int length)
    {
        if (length < 20)
        {
            return false;
        }

        var key = FastHash64(packet.AsSpan(0, length));
        var slot = (int)(key & RingMask);
        var now = Environment.TickCount64;

        var prevKey = Volatile.Read(ref _seenKeys[slot]);
        var prevTime = Volatile.Read(ref _seenTimestamps[slot]);

        if (prevKey == key && (now - prevTime) < MaxDuplicateAgeMs)
        {
            return true;
        }

        _seenKeys[slot] = key;
        _seenTimestamps[slot] = now;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong FastHash64(ReadOnlySpan<byte> data)
    {
        unchecked
        {
            var hash = 0xCBF29CE484222325UL ^ (ulong)data.Length;
            var longs = MemoryMarshal.Cast<byte, ulong>(data);
            for (var i = 0; i < longs.Length; i++)
            {
                hash = (hash ^ longs[i]) * 0x100000001B3UL;
                hash = (hash << 13) | (hash >> 51);
            }
            var remainder = data[(longs.Length * 8)..];
            for (var i = 0; i < remainder.Length; i++)
            {
                hash = (hash ^ remainder[i]) * 0x100000001B3UL;
            }
            return hash;
        }
    }
}
