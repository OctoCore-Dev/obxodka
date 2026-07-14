#pragma warning disable CA1716
using System.Buffers;
using System.Runtime.CompilerServices;
namespace obxodka.Shared.Stealth;

public static class Obfuscator
{
    private const int MinPadding = 128;
    private const int MaxPadding = 512;
    private const int MinPaddingSmall = 400;
    private const int MaxPaddingSmall = 1000;
    private static readonly byte[] t_noiseBuf = GC.AllocateUninitializedArray<byte>(4096, pinned: true);
    static Obfuscator() => Random.Shared.NextBytes(t_noiseBuf);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] Pack(byte[] packet, int packetLength, out int totalLength)
    {
        var paddingLen = packetLength > 1100
            ? Random.Shared.Next(1, 32)
            : packetLength < 100 ? Random.Shared.Next(300, 900) : Random.Shared.Next(64, 256);
        totalLength = 4 + 4 + packetLength + paddingLen;
        var buffer = ArrayPool<byte>.Shared.Rent(totalLength);
        buffer[0] = (byte)totalLength;
        buffer[1] = (byte)(totalLength >> 8);
        buffer[2] = (byte)(totalLength >> 16);
        buffer[3] = (byte)(totalLength >> 24);
        buffer[4] = (byte)packetLength;
        buffer[5] = (byte)(packetLength >> 8);
        buffer[6] = (byte)(packetLength >> 16);
        buffer[7] = (byte)(packetLength >> 24);
        unsafe
        {
            fixed (byte* src = packet, dst = buffer)
            {
                Unsafe.CopyBlockUnaligned(dst + 8, src, (uint)packetLength);
            }
        }
        var noiseOffset = Random.Shared.Next(0, t_noiseBuf.Length - paddingLen);
        unsafe
        {
            fixed (byte* noise = t_noiseBuf, dst = buffer)
            {
                Unsafe.CopyBlockUnaligned(dst + 8 + packetLength, noise + noiseOffset, (uint)paddingLen);
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
        buffer[0] = (byte)totalLength;
        buffer[1] = (byte)(totalLength >> 8);
        buffer[2] = (byte)(totalLength >> 16);
        buffer[3] = (byte)(totalLength >> 24);
        buffer[4] = (byte)packetLength;
        buffer[5] = (byte)(packetLength >> 8);
        buffer[6] = (byte)(packetLength >> 16);
        buffer[7] = (byte)(packetLength >> 24);
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
