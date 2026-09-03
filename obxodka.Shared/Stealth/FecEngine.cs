namespace obxodka.Stealth;

public sealed class FecEngine
{
    public const int ShardsPerGroup = 4;
    private const int MaxPacketSize = 2048;
    private const int GroupHistory = 32;
    private const int GroupMask = GroupHistory - 1;

    private readonly byte[] _parityAccumulator = new byte[MaxPacketSize];
    private int _maxLenInGroup;

    private sealed class DecoderGroup
    {
        public ushort GroupId;
        public readonly byte[]?[] Shards = new byte[ShardsPerGroup + 1][];
        public readonly int[] Lengths = new int[ShardsPerGroup + 1];
        public int ReceivedDataCount;
        public bool HasParity;
        public int MaxLen;
        public bool Reconstructed;

        public void Reset(ushort groupId)
        {
            GroupId = groupId;
            for (var i = 0; i <= ShardsPerGroup; i++)
            {
                if (Shards[i] != null)
                {
                    ArrayPool<byte>.Shared.Return(Shards[i]!);
                    Shards[i] = null;
                }
                Lengths[i] = 0;
            }
            ReceivedDataCount = 0;
            HasParity = false;
            MaxLen = 0;
            Reconstructed = false;
        }
    }

    private readonly DecoderGroup[] _decoderGroups = [.. Enumerable.Range(0, GroupHistory).Select(_ => new DecoderGroup())];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EncodePacket(byte[] packet, int length, byte shardIndex, out byte[]? parityBuf, out int parityLen)
    {
        parityBuf = null;
        parityLen = 0;

        if (shardIndex == 0)
        {
            Array.Clear(_parityAccumulator, 0, _maxLenInGroup);
            _maxLenInGroup = 0;
        }

        if (length > _maxLenInGroup)
        {
            _maxLenInGroup = length;
        }

        XorSpan(_parityAccumulator.AsSpan(0, length), packet.AsSpan(0, length));

        if (shardIndex == ShardsPerGroup - 1)
        {
            parityLen = _maxLenInGroup;
            parityBuf = ArrayPool<byte>.Shared.Rent(parityLen);
            Buffer.BlockCopy(_parityAccumulator, 0, parityBuf, 0, parityLen);
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ProcessIncomingShard(ushort groupId, byte shardIndex, byte[] shardData, int shardLength, Action<byte[], int> onPacketReady)
    {
        var group = _decoderGroups[groupId & GroupMask];
        if (group.GroupId != groupId)
        {
            group.Reset(groupId);
        }

        if (shardIndex < ShardsPerGroup)
        {
            var copy = ArrayPool<byte>.Shared.Rent(shardLength);
            Buffer.BlockCopy(shardData, 0, copy, 0, shardLength);
            group.Shards[shardIndex] = copy;
            group.Lengths[shardIndex] = shardLength;
            group.ReceivedDataCount++;
            if (shardLength > group.MaxLen)
            {
                group.MaxLen = shardLength;
            }

            onPacketReady(shardData, shardLength);
        }
        else if (shardIndex == ShardsPerGroup)
        {
            group.HasParity = true;
            var copy = ArrayPool<byte>.Shared.Rent(shardLength);
            Buffer.BlockCopy(shardData, 0, copy, 0, shardLength);
            group.Shards[shardIndex] = copy;
            group.Lengths[shardIndex] = shardLength;
            if (shardLength > group.MaxLen)
            {
                group.MaxLen = shardLength;
            }

            ArrayPool<byte>.Shared.Return(shardData);
        }

        if (group.HasParity && !group.Reconstructed && group.ReceivedDataCount == ShardsPerGroup - 1)
        {
            var missingIndex = -1;
            for (var i = 0; i < ShardsPerGroup; i++)
            {
                if (group.Shards[i] == null)
                {
                    missingIndex = i;
                    break;
                }
            }

            if (missingIndex >= 0)
            {
                group.Reconstructed = true;
                var recBuf = ArrayPool<byte>.Shared.Rent(group.MaxLen);
                Array.Clear(recBuf, 0, group.MaxLen);

                for (var i = 0; i <= ShardsPerGroup; i++)
                {
                    if (i != missingIndex && group.Shards[i] != null)
                    {
                        XorSpan(recBuf.AsSpan(0, group.Lengths[i]), group.Shards[i].AsSpan(0, group.Lengths[i]));
                    }
                }

                var recLen = group.MaxLen;
                if (recLen >= 20 && (recBuf[0] >> 4 == 4 || recBuf[0] >> 4 == 6))
                {
                    var actualIpLen = recBuf[0] >> 4 == 4
                        ? (recBuf[2] << 8) | recBuf[3]
                        : 40 + ((recBuf[4] << 8) | recBuf[5]);

                    if (actualIpLen > 0 && actualIpLen <= recLen)
                    {
                        recLen = actualIpLen;
                    }
                    onPacketReady(recBuf, recLen);
                }
                else
                {
                    ArrayPool<byte>.Shared.Return(recBuf);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void XorSpan(Span<byte> target, ReadOnlySpan<byte> source)
    {
        var i = 0;
        var longCount = source.Length / 8;
        var targetLongs = MemoryMarshal.Cast<byte, long>(target);
        var sourceLongs = MemoryMarshal.Cast<byte, long>(source);

        for (; i < longCount; i++)
        {
            targetLongs[i] ^= sourceLongs[i];
        }

        var remStart = longCount * 8;
        for (var j = remStart; j < source.Length; j++)
        {
            target[j] ^= source[j];
        }
    }
}
