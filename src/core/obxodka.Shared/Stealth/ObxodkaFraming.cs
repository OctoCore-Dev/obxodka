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

    public const int HeaderSize = 5;
    public const int MaxFrameSize = 65535;

    private static readonly byte[] t_noiseBuffer = GC.AllocateUninitializedArray<byte>(8192, pinned: true);

    static ObxodkaFraming() => RandomNumberGenerator.Fill(t_noiseBuffer);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryDecodeHeader(ReadOnlySpan<byte> header, out int totalLen, out int payloadLen, out byte command)
    {
        totalLen = 0;
        payloadLen = 0;
        command = 0;

        if (header.Length < HeaderSize)
        {
            return false;
        }

        var rawTotal = BinaryPrimitives.ReadUInt16BigEndian(header[..2]);
        var rawPayload = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(2, 2));
        var rawCmd = header[4];

        var decTotal = (ushort)(rawTotal ^ TotalMask);
        var decPayload = (ushort)(rawPayload ^ PayloadMask);
        var decCmd = (byte)(rawCmd ^ CommandMask);

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
    public static int CalculatePaddingLength(byte command, int payloadLength, int maxMtu = 1360)
    {
        if (command is CmdData or CmdPong)
        {
            return 0;
        }

        if (payloadLength <= 100)
        {
            return Random.Shared.Next(32, 192);
        }

        if (payloadLength <= 1000)
        {
            return Random.Shared.Next(16, 64);
        }

        var available = maxMtu - (HeaderSize + payloadLength);
        return available <= 0 ? 0 : Random.Shared.Next(0, Math.Min(available, 16));
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
        var totalLen = (ushort)(HeaderSize + payload.Length + paddingLen);
        var payloadLen = (ushort)payload.Length;

        BinaryPrimitives.WriteUInt16BigEndian(destination[..2], (ushort)(totalLen ^ TotalMask));
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2, 2), (ushort)(payloadLen ^ PayloadMask));
        destination[4] = (byte)(command ^ CommandMask);

        if (payloadLen > 0)
        {
            payload.CopyTo(destination.Slice(HeaderSize, payloadLen));
        }

        if (paddingLen > 0)
        {
            var noiseOffset = Random.Shared.Next(0, t_noiseBuffer.Length - paddingLen);
            t_noiseBuffer.AsSpan(noiseOffset, paddingLen).CopyTo(destination.Slice(HeaderSize + payloadLen, paddingLen));
        }
    }
}
