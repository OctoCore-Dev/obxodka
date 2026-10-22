namespace obxodka.Shared.Stealth;

public sealed class PacketDeduplicator
{
    private const int RingSize = 16384;
    private const int RingMask = RingSize - 1;
    private const ulong TimeMask = 0xFFFFFUL;
    private const ulong MaxDuplicateAgeMs = 500UL;

    private readonly ulong[] _entries = new ulong[RingSize];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsDuplicate(ReadOnlySpan<byte> packet)
    {
        var length = packet.Length;
        if (length < 20)
        {
            return false;
        }

        var version = packet[0] >> 4;
        if (version == 4)
        {
            var proto = packet[9];
            var isDeduplicatable = proto == 1 ||
                                   (proto == 6 && length <= 256) ||
                                   (proto == 17 && length <= 1400);
            if (!isDeduplicatable)
            {
                return false;
            }
        }
        else if (version == 6 && length >= 40)
        {
            var nextHdr = packet[6];
            var isDeduplicatable = nextHdr == 58 ||
                                   (nextHdr == 6 && length <= 256) ||
                                   (nextHdr == 17 && length <= 1400);
            if (!isDeduplicatable)
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        Span<byte> scratch = stackalloc byte[Math.Min(length, 256)];
        packet[..scratch.Length].CopyTo(scratch);
        if (version == 4 && scratch.Length >= 12)
        {
            scratch[8] = 0;
            scratch[10] = 0;
            scratch[11] = 0;
        }
        else if (version == 6 && scratch.Length >= 8)
        {
            scratch[7] = 0;
        }

        var key = FastHash64(scratch);
        var slot = (int)(key & RingMask);
        var keyTag = key & ~TimeMask;
        if (keyTag == 0)
        {
            keyTag = 1UL << 20;
        }

        var now = (ulong)Environment.TickCount64 & TimeMask;
        var newEntry = keyTag | now;
        var prevEntry = Interlocked.Exchange(ref _entries[slot], newEntry);

        if ((prevEntry & ~TimeMask) == keyTag)
        {
            var prevTime = prevEntry & TimeMask;
            var age = (now - prevTime) & TimeMask;
            if (age < MaxDuplicateAgeMs)
            {
                return true;
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsDuplicate(byte[] packet, int length) => IsDuplicate(packet.AsSpan(0, length));

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
