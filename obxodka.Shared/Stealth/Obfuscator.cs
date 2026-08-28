namespace obxodka.Shared.Stealth;

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
}
