namespace obxodka.Shared.Stealth;

public static class FechsueCodec
{
    public const int HeaderSize = 16;
    public const int TagSize = 16;
    public const int Overhead = HeaderSize + TagSize;
    public static readonly byte[] AuthMagic = [(byte)'F', (byte)'E', (byte)'C', (byte)'H'];
    public static readonly byte[] DiscMagic = [(byte)'D', (byte)'I', (byte)'S', (byte)'C'];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] PackAuth(string thumbprint, byte streamIndex, out int totalLength, ReadOnlySpan<byte> magic = default)
    {
        var tpBytes = Encoding.UTF8.GetBytes(thumbprint);
        totalLength = 17 + tpBytes.Length;
        var buf = ArrayPool<byte>.Shared.Rent(totalLength);
        if (magic.IsEmpty)
        {
            AuthMagic.CopyTo(buf.AsSpan(0, 4));
        }
        else
        {
            magic[..4].CopyTo(buf.AsSpan(0, 4));
        }
        var hash = SHA256.HashData(tpBytes);
        var sessionId = BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), sessionId);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8, 8), DateTime.UtcNow.Ticks);
        buf[16] = streamIndex;
        tpBytes.CopyTo(buf.AsSpan(17, tpBytes.Length));
        return buf;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryUnpackAuth(ReadOnlySpan<byte> buffer, out string thumbprint, out uint sessionId, out byte streamIndex, ReadOnlySpan<byte> expectedMagic = default)
    {
        thumbprint = string.Empty;
        sessionId = 0;
        streamIndex = 0;
        var magic = expectedMagic.IsEmpty ? (ReadOnlySpan<byte>)AuthMagic : expectedMagic;
        if (buffer.Length < 17 + 8 || !buffer[..4].SequenceEqual(magic))
        {
            return false;
        }

        sessionId = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(4, 4));
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
