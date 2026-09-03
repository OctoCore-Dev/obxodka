namespace obxodka.Stealth;

public static class FechsueCodec
{
    public const int HeaderSize = 16;
    public const int TagSize = 16;
    public const int Overhead = HeaderSize + TagSize;
    public const uint StealthAuthMask = 0xA55A3C7E;
    public const uint StealthDiscMask = 0x5AA5C381;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] PackAuth(string thumbprint, byte streamIndex, out int totalLength)
    {
        var tpBytes = Encoding.UTF8.GetBytes(thumbprint);
        totalLength = 17 + tpBytes.Length;
        var buf = ArrayPool<byte>.Shared.Rent(totalLength);
        var hash = SHA256.HashData(tpBytes);
        var sessionId = BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), sessionId ^ StealthAuthMask);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), sessionId);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8, 8), DateTime.UtcNow.Ticks);
        buf[16] = streamIndex;
        tpBytes.CopyTo(buf.AsSpan(17, tpBytes.Length));
        return buf;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryUnpackAuth(ReadOnlySpan<byte> buffer, out string thumbprint, out uint sessionId, out byte streamIndex)
    {
        thumbprint = string.Empty;
        sessionId = 0;
        streamIndex = 0;
        if (buffer.Length < 17 + 8)
        {
            return false;
        }

        var token = BinaryPrimitives.ReadUInt32LittleEndian(buffer[..4]);
        sessionId = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(4, 4));
        if ((token ^ sessionId) != StealthAuthMask)
        {
            return false;
        }

        var ticks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(8, 8));
        var diff = Math.Abs(DateTime.UtcNow.Ticks - ticks);
        if (diff > TimeSpan.TicksPerDay)
        {
            return false;
        }

        streamIndex = buffer[16];
        thumbprint = Encoding.UTF8.GetString(buffer[17..]);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] PackDisc(string thumbprint, out int totalLength)
    {
        var tpBytes = Encoding.UTF8.GetBytes(thumbprint);
        totalLength = 17 + tpBytes.Length;
        var buf = ArrayPool<byte>.Shared.Rent(totalLength);
        var hash = SHA256.HashData(tpBytes);
        var sessionId = BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), sessionId ^ StealthDiscMask);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), sessionId);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8, 8), DateTime.UtcNow.Ticks);
        buf[16] = 0xFF;
        tpBytes.CopyTo(buf.AsSpan(17, tpBytes.Length));
        return buf;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryUnpackDisc(ReadOnlySpan<byte> buffer, out string thumbprint, out uint sessionId)
    {
        thumbprint = string.Empty;
        sessionId = 0;
        if (buffer.Length < 17 + 8)
        {
            return false;
        }

        var token = BinaryPrimitives.ReadUInt32LittleEndian(buffer[..4]);
        sessionId = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(4, 4));
        if ((token ^ sessionId) != StealthDiscMask)
        {
            return false;
        }

        var ticks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(8, 8));
        var diff = Math.Abs(DateTime.UtcNow.Ticks - ticks);
        if (diff > TimeSpan.TicksPerDay)
        {
            return false;
        }

        thumbprint = Encoding.UTF8.GetString(buffer[17..]);
        return true;
    }

    private static long t_nonceCounter = Random.Shared.NextInt64();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] Pack(
        byte[] payload,
        int length,
        uint sessionId,
        AesGcm crypto,
        out int totalLength)
    {
        totalLength = HeaderSize + length + TagSize;
        var buffer = ArrayPool<byte>.Shared.Rent(totalLength);

        var nonce = buffer.AsSpan(0, 12);
        var counter = (ulong)Interlocked.Increment(ref t_nonceCounter);
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[..8], counter);
        BinaryPrimitives.WriteUInt32LittleEndian(nonce[8..12], sessionId);

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12, 4), sessionId);

        var associatedData = buffer.AsSpan(12, 4);
        var ciphertext = buffer.AsSpan(HeaderSize, length);
        var tag = buffer.AsSpan(HeaderSize + length, TagSize);

        crypto.Encrypt(nonce, payload.AsSpan(0, length), ciphertext, tag, associatedData);
        return buffer;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryUnpack(
        byte[] buffer,
        int totalLength,
        AesGcm crypto,
        out uint sessionId,
        out byte[]? payload,
        out int realLength)
    {
        sessionId = 0;
        payload = null;
        realLength = 0;

        if (totalLength < Overhead)
        {
            return false;
        }

        var nonce = buffer.AsSpan(0, 12);
        sessionId = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(12, 4));
        realLength = totalLength - Overhead;

        var associatedData = buffer.AsSpan(12, 4);
        var ciphertext = buffer.AsSpan(HeaderSize, realLength);
        var tag = buffer.AsSpan(HeaderSize + realLength, TagSize);

        payload = ArrayPool<byte>.Shared.Rent(realLength);
        try
        {
            crypto.Decrypt(nonce, ciphertext, tag, payload.AsSpan(0, realLength), associatedData);
            return true;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(payload);
            payload = null;
            return false;
        }
    }

    public const byte FrameTypeRaw = 0x00;
    public const byte FrameTypeFecData = 0x01;
    public const byte FrameTypeFecParity = 0x02;
    public const byte DefaultFecGroupSize = 4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] PackFecData(
        byte[] payload,
        int length,
        ushort groupId,
        byte indexInGroup,
        byte groupSize,
        uint sessionId,
        AesGcm crypto,
        out int totalLength)
    {
        var headerOffset = 5;
        var rawLength = headerOffset + length;
        var rawBuf = ArrayPool<byte>.Shared.Rent(rawLength);
        rawBuf[0] = FrameTypeFecData;
        BinaryPrimitives.WriteUInt16LittleEndian(rawBuf.AsSpan(1, 2), groupId);
        rawBuf[3] = indexInGroup;
        rawBuf[4] = groupSize;
        Buffer.BlockCopy(payload, 0, rawBuf, headerOffset, length);

        var packed = Pack(rawBuf, rawLength, sessionId, crypto, out totalLength);
        ArrayPool<byte>.Shared.Return(rawBuf);
        return packed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] PackFecParity(
        byte[] parityPayload,
        int parityLength,
        ushort groupId,
        byte groupSize,
        uint sessionId,
        AesGcm crypto,
        out int totalLength)
    {
        var headerOffset = 6;
        var rawLength = headerOffset + parityLength;
        var rawBuf = ArrayPool<byte>.Shared.Rent(rawLength);
        rawBuf[0] = FrameTypeFecParity;
        BinaryPrimitives.WriteUInt16LittleEndian(rawBuf.AsSpan(1, 2), groupId);
        rawBuf[3] = groupSize;
        BinaryPrimitives.WriteUInt16LittleEndian(rawBuf.AsSpan(4, 2), (ushort)parityLength);
        Buffer.BlockCopy(parityPayload, 0, rawBuf, headerOffset, parityLength);

        var packed = Pack(rawBuf, rawLength, sessionId, crypto, out totalLength);
        ArrayPool<byte>.Shared.Return(rawBuf);
        return packed;
    }

    public sealed class FecEncoder(byte groupSize = DefaultFecGroupSize)
    {
        private readonly byte _groupSize = groupSize > 0 ? groupSize : DefaultFecGroupSize;
        private ushort _currentGroupId;
        private byte _currentIndex;
        private readonly byte[] _parityAccumulator = new byte[2048];
        private int _maxPacketLengthInGroup;
        private readonly Lock _lock = new();

        public (byte[] packet, int length, byte[]? parityPacket, int parityLength) Encode(
            byte[] rawPacket,
            int rawLength,
            uint sessionId,
            AesGcm crypto)
        {
            lock (_lock)
            {
                var groupId = _currentGroupId;
                var index = _currentIndex;
                var gSize = _groupSize;

                if (index == 0)
                {
                    Array.Clear(_parityAccumulator, 0, _maxPacketLengthInGroup);
                    _maxPacketLengthInGroup = 0;
                }

                if (rawLength > _maxPacketLengthInGroup)
                {
                    _maxPacketLengthInGroup = rawLength;
                }

                for (var i = 0; i < rawLength; i++)
                {
                    _parityAccumulator[i] ^= rawPacket[i];
                }

                var dataPacked = PackFecData(rawPacket, rawLength, groupId, index, gSize, sessionId, crypto, out var dataLen);

                byte[]? parityPacked = null;
                var parityLen = 0;

                _currentIndex++;
                if (_currentIndex >= _groupSize)
                {
                    parityPacked = PackFecParity(_parityAccumulator, _maxPacketLengthInGroup, groupId, gSize, sessionId, crypto, out parityLen);
                    _currentIndex = 0;
                    _currentGroupId++;
                }

                return (dataPacked, dataLen, parityPacked, parityLen);
            }
        }
    }

    public sealed class FecDecoder
    {
        private readonly struct FecSlot(byte[]? packet, int length, bool received)
        {
            public readonly byte[]? Packet = packet;
            public readonly int Length = length;
            public readonly bool Received = received;
        }

        private sealed class FecGroup(byte groupSize)
        {
            public readonly byte GroupSize = groupSize;
            public readonly FecSlot[] Slots = new FecSlot[groupSize];
            public byte[]? Parity;
            public int ParityLength;
            public int ReceivedCount;
            public bool ParityReceived;
            public bool Recovered;
        }

        private readonly Dictionary<ushort, FecGroup> _groups = [];
        private readonly Lock _lock = new();
        private const int MaxTrackedGroups = 64;

        public bool ProcessPayload(
            byte[] decryptedPayload,
            int decryptedLength,
            out byte[]? directPacket,
            out int directLength,
            out byte[]? recoveredPacket,
            out int recoveredLength)
        {
            directPacket = null;
            directLength = 0;
            recoveredPacket = null;
            recoveredLength = 0;

            if (decryptedLength < 5)
            {
                directPacket = ArrayPool<byte>.Shared.Rent(decryptedLength);
                Buffer.BlockCopy(decryptedPayload, 0, directPacket, 0, decryptedLength);
                directLength = decryptedLength;
                return true;
            }

            var frameType = decryptedPayload[0];
            if (frameType == FrameTypeFecData)
            {
                var groupId = BinaryPrimitives.ReadUInt16LittleEndian(decryptedPayload.AsSpan(1, 2));
                var index = decryptedPayload[3];
                var groupSize = decryptedPayload[4];
                var dataLen = decryptedLength - 5;

                directPacket = ArrayPool<byte>.Shared.Rent(dataLen);
                Buffer.BlockCopy(decryptedPayload, 5, directPacket, 0, dataLen);
                directLength = dataLen;

                lock (_lock)
                {
                    CleanupOldGroups(groupId);

                    if (!_groups.TryGetValue(groupId, out var group))
                    {
                        group = new FecGroup(groupSize);
                        _groups[groupId] = group;
                    }

                    if (index < group.GroupSize && !group.Slots[index].Received)
                    {
                        var copy = new byte[dataLen];
                        Buffer.BlockCopy(decryptedPayload, 5, copy, 0, dataLen);
                        group.Slots[index] = new FecSlot(copy, dataLen, true);
                        group.ReceivedCount++;

                        TryRecoverMissing(group, out recoveredPacket, out recoveredLength);
                    }
                }
                return true;
            }
            else if (frameType == FrameTypeFecParity)
            {
                if (decryptedLength < 6)
                {
                    return false;
                }

                var groupId = BinaryPrimitives.ReadUInt16LittleEndian(decryptedPayload.AsSpan(1, 2));
                var groupSize = decryptedPayload[3];
                var parityLen = (int)BinaryPrimitives.ReadUInt16LittleEndian(decryptedPayload.AsSpan(4, 2));
                var actualParityLen = Math.Min(parityLen, decryptedLength - 6);

                lock (_lock)
                {
                    CleanupOldGroups(groupId);

                    if (!_groups.TryGetValue(groupId, out var group))
                    {
                        group = new FecGroup(groupSize);
                        _groups[groupId] = group;
                    }

                    if (!group.ParityReceived)
                    {
                        group.Parity = new byte[actualParityLen];
                        Buffer.BlockCopy(decryptedPayload, 6, group.Parity, 0, actualParityLen);
                        group.ParityLength = actualParityLen;
                        group.ParityReceived = true;

                        TryRecoverMissing(group, out recoveredPacket, out recoveredLength);
                    }
                }
                return true;
            }
            else
            {
                directPacket = ArrayPool<byte>.Shared.Rent(decryptedLength);
                Buffer.BlockCopy(decryptedPayload, 0, directPacket, 0, decryptedLength);
                directLength = decryptedLength;
                return true;
            }
        }

        private static void TryRecoverMissing(
            FecGroup group,
            out byte[]? recoveredPacket,
            out int recoveredLength)
        {
            recoveredPacket = null;
            recoveredLength = 0;

            if (group.Recovered || !group.ParityReceived || group.Parity == null)
            {
                return;
            }

            if (group.ReceivedCount == group.GroupSize - 1)
            {
                var missingIndex = -1;
                for (var i = 0; i < group.GroupSize; i++)
                {
                    if (!group.Slots[i].Received)
                    {
                        missingIndex = i;
                        break;
                    }
                }

                if (missingIndex >= 0)
                {
                    var rec = new byte[group.ParityLength];
                    Buffer.BlockCopy(group.Parity, 0, rec, 0, group.ParityLength);

                    for (var i = 0; i < group.GroupSize; i++)
                    {
                        if (i != missingIndex && group.Slots[i].Received && group.Slots[i].Packet != null)
                        {
                            var slotPkt = group.Slots[i].Packet!;
                            for (var b = 0; b < slotPkt.Length; b++)
                            {
                                rec[b] ^= slotPkt[b];
                            }
                        }
                    }

                    group.Recovered = true;
                    recoveredPacket = ArrayPool<byte>.Shared.Rent(group.ParityLength);
                    Buffer.BlockCopy(rec, 0, recoveredPacket, 0, group.ParityLength);
                    recoveredLength = group.ParityLength;
                }
            }
        }

        private void CleanupOldGroups(ushort currentGroupId)
        {
            if (_groups.Count > MaxTrackedGroups)
            {
                var keysToRemove = new List<ushort>();
                foreach (var gId in _groups.Keys)
                {
                    var diff = (ushort)(currentGroupId - gId);
                    if (diff > MaxTrackedGroups)
                    {
                        keysToRemove.Add(gId);
                    }
                }
                foreach (var k in keysToRemove)
                {
                    _ = _groups.Remove(k);
                }
            }
        }
    }
}
