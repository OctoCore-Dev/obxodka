namespace obxodka.Stealth;

public static class Obfuscator
{
    private static readonly byte[] t_noiseBuf = GC.AllocateUninitializedArray<byte>(4096, pinned: true);

    static Obfuscator() => Random.Shared.NextBytes(t_noiseBuf);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] Pack(byte[] packet, int packetLength, out int totalLength)
    {
        var paddingLen = packetLength == 9 && packet[0] == 0x99
            ? 0
            : packetLength >= 20 && (((packet[0] >> 4) == 4 && (packet[9] == 17 || packet[9] == 1)) || ((packet[0] >> 4) == 6 && (packet[6] == 17 || packet[6] == 58)))
            ? 0
            : packetLength > 1100
                ? Random.Shared.Next(1, 32)
                : packetLength <= 100 ? Random.Shared.Next(16, 48) : Random.Shared.Next(32, 128);

        totalLength = 4 + 4 + packetLength + paddingLen;
        var buffer = ArrayPool<byte>.Shared.Rent(totalLength);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), totalLength);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), packetLength);

        unsafe
        {
            fixed (byte* src = packet, dst = buffer)
            {
                Unsafe.CopyBlockUnaligned(dst + 8, src, (uint)packetLength);
            }
        }

        if (paddingLen > 0)
        {
            var noiseOffset = Random.Shared.Next(0, t_noiseBuf.Length - paddingLen);
            unsafe
            {
                fixed (byte* noise = t_noiseBuf, dst = buffer)
                {
                    Unsafe.CopyBlockUnaligned(dst + 8 + packetLength, noise + noiseOffset, (uint)paddingLen);
                }
            }
        }

        return buffer;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] PackSmart(byte[] packet, int packetLength, out int totalLength, bool isProxied)
    {
        if (!isProxied)
        {
            return Pack(packet, packetLength, out totalLength);
        }

        totalLength = 4 + 4 + packetLength;
        var buffer = ArrayPool<byte>.Shared.Rent(totalLength);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), totalLength);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), packetLength);

        unsafe
        {
            fixed (byte* src = packet, dst = buffer)
            {
                Unsafe.CopyBlockUnaligned(dst + 8, src, (uint)packetLength);
            }
        }

        return buffer;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryUnpack(
        ReadOnlySpan<byte> frame,
        out int realLength,
        out ReadOnlySpan<byte> payload)
    {
        realLength = 0;
        payload = default;

        if (frame.Length < 8)
        {
            return false;
        }

        var totalLen = BinaryPrimitives.ReadInt32LittleEndian(frame[..4]);
        realLength = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(4, 4));

        if (totalLen < 8 || realLength < 0 || realLength > totalLen - 8 || frame.Length < totalLen)
        {
            realLength = 0;
            return false;
        }

        payload = frame.Slice(8, realLength);
        return true;
    }

    public static async ValueTask<(byte[]? packet, int length)> ReadPacketAsync(
        Stream stream,
        byte[] headerBuffer,
        CancellationToken ct)
    {
        await stream.ReadExactlyAsync(headerBuffer.AsMemory(0, 8), ct).ConfigureAwait(false);
        var totalLen = BinaryPrimitives.ReadInt32LittleEndian(headerBuffer.AsSpan(0, 4));
        var realLen = BinaryPrimitives.ReadInt32LittleEndian(headerBuffer.AsSpan(4, 4));

        if (totalLen is <= 0 or > 1048576 || realLen < 0 || realLen > totalLen - 8)
        {
            return (null, 0);
        }

        var packet = ArrayPool<byte>.Shared.Rent(realLen);
        try
        {
            await stream.ReadExactlyAsync(packet.AsMemory(0, realLen), ct).ConfigureAwait(false);
            var paddingLen = totalLen - 8 - realLen;
            if (paddingLen > 0)
            {
                var trash = ArrayPool<byte>.Shared.Rent(paddingLen);
                try
                {
                    await stream.ReadExactlyAsync(trash.AsMemory(0, paddingLen), ct).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(trash);
                }
            }

            return (packet, realLen);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(packet);
            throw;
        }
    }
}
