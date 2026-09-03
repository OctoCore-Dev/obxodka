namespace obxodka.Stealth;

public static class QuicInitialBuilder
{
    public const uint QuicVersion1 = 0x00000001;
    public const uint QuicVersionDraft29 = 0xff00001d;

    public const byte PacketTypeInitial = 0xc0;
    public const byte PacketTypeRetry = 0xf0;
    public const byte PacketTypeHandshake = 0xe0;
    public const byte PacketTypeZeroRtt = 0xd0;

    private static readonly Random t_random = Random.Shared;
    private static long t_packetNumber = t_random.NextInt64();

    public static byte[] BuildInitialPacket(
        ReadOnlySpan<byte> destinationConnectionId,
        ReadOnlySpan<byte> sourceConnectionId,
        ReadOnlySpan<byte> clientHello,
        ReadOnlySpan<byte> token = default,
        uint version = QuicVersion1,
        long? packetNumber = null)
    {
        var dcidLen = destinationConnectionId.Length;
        var scidLen = sourceConnectionId.Length;
        var tokenLen = (ulong)token.Length;

        var pn = packetNumber ?? Interlocked.Increment(ref t_packetNumber);
        var pnBytes = GetPacketNumberBytes(pn);
        var pnLen = pnBytes.Length;

        var headerSize = 1 + 4 + 1 + dcidLen + 1 + scidLen + VarIntSize(tokenLen) + (int)tokenLen;
        var payloadSize = clientHello.Length + 16;
        var totalSize = headerSize + payloadSize + pnLen;

        var buffer = new byte[totalSize];
        var offset = 0;

        buffer[offset++] = (byte)(PacketTypeInitial | ((pnLen - 1) & 0x03));

        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset, 4), version);
        offset += 4;

        buffer[offset++] = (byte)dcidLen;
        destinationConnectionId.CopyTo(buffer.AsSpan(offset, dcidLen));
        offset += dcidLen;

        buffer[offset++] = (byte)scidLen;
        sourceConnectionId.CopyTo(buffer.AsSpan(offset, scidLen));
        offset += scidLen;

        offset += WriteVarInt(buffer.AsSpan(offset), tokenLen);
        token.CopyTo(buffer.AsSpan(offset, (int)tokenLen));
        offset += (int)tokenLen;

        var pnOffset = offset;
        offset += pnLen;

        var ciphertextOffset = offset;
        offset += clientHello.Length;

        var initialSecret = DeriveInitialSecret(destinationConnectionId);
        using var aead = new AesGcm(initialSecret, 16);
        var nonce = BuildNonce(pn, pnLen);

        pnBytes.CopyTo(buffer.AsSpan(pnOffset, pnLen));
        var aad = buffer.AsSpan(0, pnOffset + pnLen);

        var tag = new byte[16];
        aead.Encrypt(nonce, clientHello, buffer.AsSpan(ciphertextOffset, clientHello.Length), tag, aad);

        tag.CopyTo(buffer.AsSpan(offset, 16));

        return buffer;
    }

    private static byte[] DeriveInitialSecret(ReadOnlySpan<byte> dcid)
    {
        var salt = new byte[] { 0x38, 0x76, 0x2c, 0xf7, 0xf5, 0x59, 0x34, 0xb3, 0xae, 0xad, 0x7f };

        var initialSecret = HKDF.DeriveKey(HashAlgorithmName.SHA256, dcid.ToArray(), 32, salt);

        var label = "client in"u8.ToArray();
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, initialSecret, 32, [], label);
    }

    private static byte[] BuildNonce(long packetNumber, int pnLen)
    {
        var nonce = new byte[12];
        var pnBytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(pnBytes, (ulong)packetNumber);
        pnBytes.AsSpan(8 - pnLen).CopyTo(nonce.AsSpan(12 - pnLen));
        return nonce;
    }

    private static byte[] GetPacketNumberBytes(long packetNumber)
    {
        return packetNumber < 0x100
            ? [(byte)packetNumber]
            : packetNumber < 0x10000
            ? [(byte)(packetNumber >> 8), (byte)packetNumber]
            : packetNumber < 0x1000000
            ? [(byte)(packetNumber >> 16), (byte)(packetNumber >> 8), (byte)packetNumber]
            : [(byte)(packetNumber >> 24), (byte)(packetNumber >> 16), (byte)(packetNumber >> 8), (byte)packetNumber];
    }

    private static int VarIntSize(ulong value) => value < 64 ? 1 : value < 16384 ? 2 : value < 1073741824 ? 4 : 8;

    private static int WriteVarInt(Span<byte> buffer, ulong value)
    {
        if (value < 64)
        {
            buffer[0] = (byte)value;
            return 1;
        }
        if (value < 16384)
        {
            BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)(value | 0x4000));
            return 2;
        }
        if (value < 1073741824)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)(value | 0x80000000));
            return 4;
        }
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value | 0xc000000000000000);
        return 8;
    }
}

