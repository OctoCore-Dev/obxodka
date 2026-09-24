using System.Runtime.CompilerServices;

namespace obxodka.Shared.Stealth;

public sealed class ChameleonState(uint sessionSeed, ulong initialSequence = 0)
{
    private ulong _sequence = initialSequence;
    private readonly uint _sessionSeed = sessionSeed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint NextMask()
    {
        var seq = Interlocked.Increment(ref _sequence);
        return (uint)(SplitMix64(seq) ^ _sessionSeed);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int NextJitter(int maxPadding = 48)
    {
        if (maxPadding <= 0)
        {
            return 0;
        }

        var seq = _sequence;
        var mixed = SplitMix64(seq + _sessionSeed);
        return (int)(mixed % (ulong)(maxPadding + 1));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int NextSplitPosition(int min = 1, int max = 4)
    {
        if (min >= max)
        {
            return min;
        }

        var seq = _sequence;
        var mixed = SplitMix64(seq ^ 0x5555555555555555UL);
        var range = (ulong)(max - min + 1);
        return min + (int)(mixed % range);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong SplitMix64(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }
}
