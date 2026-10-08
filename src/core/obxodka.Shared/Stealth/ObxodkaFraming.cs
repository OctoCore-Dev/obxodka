namespace obxodka.Shared.Stealth;

public static class ObxodkaFraming
{
    public const ushort TotalMask = 0x7E3A;
    public const ushort PayloadMask = 0x4C1F;
    public const byte CommandMask = 0xA5;

    public const byte CmdData = 0x01;
    public const byte CmdPing = 0x02;
    public const byte CmdPong = 0x03;
    public const byte CmdHandshake = 0x04;
    public const byte CmdHandshakeResponse = 0x05;
    public const byte CmdDisconnect = 0x06;
    public const byte CmdMtuSync = 0x07;

    public const int HeaderSize = 6;
    public const int MaxFrameSize = 65535;

    private static readonly byte[] t_noiseBuffer = GC.AllocateUninitializedArray<byte>(8192, pinned: true);

    static ObxodkaFraming() => RandomNumberGenerator.Fill(t_noiseBuffer);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MaskPayload(Span<byte> payload, byte salt)
    {
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] ^= (byte)(salt + (i * 37) + ((i >> 3) ^ 0x9D));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UnmaskPayload(Span<byte> payload, byte salt) => MaskPayload(payload, salt);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryDecodeHeader(ReadOnlySpan<byte> header, out int totalLen, out int payloadLen, out byte command, out byte salt)
    {
        totalLen = 0;
        payloadLen = 0;
        command = 0;
        salt = 0;

        if (header.Length < HeaderSize)
        {
            return false;
        }

        salt = header[0];
        var rawTotal = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(1, 2));
        var rawPayload = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(3, 2));
        var rawCmd = header[5];

        var totalMask = (ushort)(TotalMask ^ ((salt << 8) | salt));
        var payloadMask = (ushort)(PayloadMask ^ ((salt << 8) | (salt ^ 0x5C)));
        var cmdMask = (byte)(CommandMask ^ salt);

        var decTotal = (ushort)(rawTotal ^ totalMask);
        var decPayload = (ushort)(rawPayload ^ payloadMask);
        var decCmd = (byte)(rawCmd ^ cmdMask);

        if (decTotal < HeaderSize || decPayload > decTotal - HeaderSize)
        {
            return false;
        }

        totalLen = decTotal;
        payloadLen = decPayload;
        command = decCmd;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryDecodeHeader(ReadOnlySpan<byte> header, out int totalLen, out int payloadLen, out byte command) =>
        TryDecodeHeader(header, out totalLen, out payloadLen, out command, out _);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalculatePaddingLength(byte command, int payloadLength, int maxMtu = 1360)
    {
        if (command is CmdPong or CmdMtuSync)
        {
            return 0;
        }

        if (command == CmdData)
        {
            var available = maxMtu - (HeaderSize + payloadLength);
            return available <= 0
                ? 0
                : payloadLength <= 128
                    ? Random.Shared.Next(8, Math.Min(available, 64))
                    : Random.Shared.Next(0, Math.Min(available, 16));
        }

        if (payloadLength <= 100)
        {
            return Random.Shared.Next(32, 192);
        }

        if (payloadLength <= 1000)
        {
            return Random.Shared.Next(16, 64);
        }

        var avail = maxMtu - (HeaderSize + payloadLength);
        return avail <= 0 ? 0 : Random.Shared.Next(0, Math.Min(avail, 16));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalculatePaddingLength(int payloadLength, int maxMtu = 1360) =>
        CalculatePaddingLength(CmdData, payloadLength, maxMtu);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] Pack(byte command, ReadOnlySpan<byte> payload, out int totalLen, int maxMtu = 1360)
    {
        var paddingLen = CalculatePaddingLength(command, payload.Length, maxMtu);
        totalLen = HeaderSize + payload.Length + paddingLen;

        var buffer = ArrayPool<byte>.Shared.Rent(totalLen);
        WriteFrame(buffer.AsSpan(0, totalLen), command, payload, paddingLen);
        return buffer;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteFrame(Span<byte> destination, byte command, ReadOnlySpan<byte> payload, int paddingLen)
    {
        var salt = (byte)Random.Shared.Next(0, 256);
        var totalLen = (ushort)(HeaderSize + payload.Length + paddingLen);
        var payloadLen = (ushort)payload.Length;

        destination[0] = salt;
        var totalMask = (ushort)(TotalMask ^ ((salt << 8) | salt));
        var payloadMask = (ushort)(PayloadMask ^ ((salt << 8) | (salt ^ 0x5C)));
        var cmdMask = (byte)(CommandMask ^ salt);

        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(1, 2), (ushort)(totalLen ^ totalMask));
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(3, 2), (ushort)(payloadLen ^ payloadMask));
        destination[5] = (byte)(command ^ cmdMask);

        if (payloadLen > 0)
        {
            var payloadDest = destination.Slice(HeaderSize, payloadLen);
            payload.CopyTo(payloadDest);
            MaskPayload(payloadDest, salt);
        }

        if (paddingLen > 0)
        {
            var noiseOffset = Random.Shared.Next(0, t_noiseBuffer.Length - paddingLen);
            t_noiseBuffer.AsSpan(noiseOffset, paddingLen).CopyTo(destination.Slice(HeaderSize + payloadLen, paddingLen));
        }
    }
}
