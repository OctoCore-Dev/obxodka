namespace obxodka.Shared.Stealth;

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
}
