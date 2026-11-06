namespace obxodka.Shared.Stealth.ClientHello;

//генерация структуры tls 1.3 clienthello для safari
public static class SafariHello
{
    private static readonly ushort[] s_safariCiphers =
    [
        0x1301, // TLS_AES_128_GCM_SHA256
        0x1302, // TLS_AES_256_GCM_SHA384
        0x1303, // TLS_CHACHA20_POLY1305_SHA256
        0xc02b, // ECDHE-ECDSA-AES128-GCM-SHA256
        0xc02c, // ECDHE-ECDSA-AES256-GCM-SHA384
        0xc02f, // ECDHE-RSA-AES128-GCM-SHA256
        0xc030, // ECDHE-RSA-AES256-GCM-SHA384
        0xcca9, // ECDHE-ECDSA-CHACHA20-POLY1305
        0xcca8  // ECDHE-RSA-CHACHA20-POLY1305
    ];

    private static readonly ushort[] s_safariSignatures =
    [
        0x0403, // ecdsa_secp256r1_sha256
        0x0503, // ecdsa_secp384r1_sha384
        0x0603, // ecdsa_secp521r1_sha512
        0x0807, // ed25519
        0x0804, // rsa_pss_rsae_sha256
        0x0805, // rsa_pss_rsae_sha384
        0x0806, // rsa_pss_rsae_sha512
        0x0401  // rsa_pkcs1_sha256
    ];

    public static byte[] Build(
        ReadOnlySpan<byte> sni,
        ReadOnlySpan<byte> sessionId = default,
        bool isMobile = true)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(1024);
        var offset = 0;

        //заголовок handshake clienthello
        buffer[offset++] = 0x01;

        var handshakeLengthPos = offset;
        offset += 3;

        //версия legacy tls 1.2
        buffer[offset++] = 0x03;
        buffer[offset++] = 0x03;

        //случайные 32 байта клиента
        RandomNumberGenerator.Fill(buffer.AsSpan(offset, 32));
        offset += 32;

        //идентификатор сессии session id
        if (sessionId.Length > 0)
        {
            buffer[offset++] = (byte)sessionId.Length;
            sessionId.CopyTo(buffer.AsSpan(offset));
            offset += sessionId.Length;
        }
        else
        {
            buffer[offset++] = 32;
            RandomNumberGenerator.Fill(buffer.AsSpan(offset, 32));
            offset += 32;
        }

        //шифрокомплекты grease и safari
        var greaseCipher = TlsGreaseHelper.GetRandomGrease();
        var cipherListByteLen = (1 + s_safariCiphers.Length) * 2;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), (ushort)cipherListByteLen);
        offset += 2;

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), greaseCipher);
        offset += 2;

        foreach (var cipher in s_safariCiphers)
        {
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), cipher);
            offset += 2;
        }

        //сжатие null
        buffer[offset++] = 0x01;
        buffer[offset++] = 0x00;

        //расширения tls
        var extLengthPos = offset;
        offset += 2;

        WriteExtensions(buffer, ref offset, sni, isMobile);

        var extLength = offset - extLengthPos - 2;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(extLengthPos, 2), (ushort)extLength);

        var handshakeLength = offset - handshakeLengthPos - 3;
        buffer[handshakeLengthPos] = (byte)(handshakeLength >> 16);
        buffer[handshakeLengthPos + 1] = (byte)(handshakeLength >> 8);
        buffer[handshakeLengthPos + 2] = (byte)handshakeLength;

        //обертка в tls record layer
        var record = GC.AllocateUninitializedArray<byte>(offset + 5);
        record[0] = 0x16;
        record[1] = 0x03;
        record[2] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(3, 2), (ushort)offset);
        buffer.AsSpan(0, offset).CopyTo(record.AsSpan(5));

        return record;
    }

    private static void WriteExtensions(byte[] buffer, ref int offset, ReadOnlySpan<byte> sni, bool isMobile)
    {
        _ = isMobile;
        var greaseExt = TlsGreaseHelper.GetRandomGrease();

        WriteExtension(buffer, ref offset, greaseExt, []); //grease
        if (sni.Length > 0)
        {
            WriteServerName(buffer, ref offset, sni); //sni
        }

        WriteExtension(buffer, ref offset, 0x0017, []); //extended master secret
        WriteExtension(buffer, ref offset, 0xff01, [0x00]); //renegotiation info
        WriteSupportedGroups(buffer, ref offset); //supported groups
        WriteExtension(buffer, ref offset, 0x000b, [0x01, 0x00]); //ec point formats
        WriteExtension(buffer, ref offset, 0x0023, []); //session ticket
        WriteAlpn(buffer, ref offset); //alpn
        WriteExtension(buffer, ref offset, 0x0005, [0x01, 0x00, 0x00, 0x00, 0x00]); //status request
        WriteKeyShare(buffer, ref offset); //key share
        WriteSupportedVersions(buffer, ref offset); //supported versions
        WriteSignatureAlgorithms(buffer, ref offset); //signature algorithms
        WriteExtension(buffer, ref offset, 0x002d, [0x01, 0x01]); //psk modes
    }

    private static void WriteExtension(byte[] buffer, ref int offset, ushort type, ReadOnlySpan<byte> data)
    {
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), type);
        offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), (ushort)data.Length);
        offset += 2;
        if (data.Length > 0)
        {
            data.CopyTo(buffer.AsSpan(offset));
            offset += data.Length;
        }
    }

    private static void WriteServerName(byte[] buffer, ref int offset, ReadOnlySpan<byte> sni)
    {
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), 0x0000);
        offset += 2;

        var extLengthPos = offset;
        offset += 2;

        var listLengthPos = offset;
        offset += 2;

        buffer[offset++] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), (ushort)sni.Length);
        offset += 2;
        sni.CopyTo(buffer.AsSpan(offset));
        offset += sni.Length;

        var listLength = offset - listLengthPos - 2;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(listLengthPos, 2), (ushort)listLength);

        var extLength = offset - extLengthPos - 2;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(extLengthPos, 2), (ushort)extLength);
    }

    private static void WriteSupportedGroups(byte[] buffer, ref int offset)
    {
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), 0x000a);
        offset += 2;

        var extLengthPos = offset;
        offset += 2;

        var listLengthPos = offset;
        offset += 2;

        var greaseGroup = TlsGreaseHelper.GetRandomGrease();
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), greaseGroup);
        offset += 2;

        ReadOnlySpan<ushort> groups = [0x001d, 0x0017, 0x0018, 0x0019];
        foreach (var group in groups)
        {
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), group);
            offset += 2;
        }

        var listLength = offset - listLengthPos - 2;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(listLengthPos, 2), (ushort)listLength);

        var extLength = offset - extLengthPos - 2;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(extLengthPos, 2), (ushort)extLength);
    }

    private static void WriteAlpn(byte[] buffer, ref int offset)
    {
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), 0x0010);
        offset += 2;

        ReadOnlySpan<byte> alpnPayload = [0x00, 0x0c, 0x02, 0x68, 0x32, 0x08, 0x68, 0x74, 0x74, 0x70, 0x2f, 0x31, 0x2e, 0x31];
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), (ushort)alpnPayload.Length);
        offset += 2;
        alpnPayload.CopyTo(buffer.AsSpan(offset));
        offset += alpnPayload.Length;
    }

    private static void WriteKeyShare(byte[] buffer, ref int offset)
    {
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), 0x0033);
        offset += 2;

        var extLengthPos = offset;
        offset += 2;

        var listLengthPos = offset;
        offset += 2;

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), 0x001d);
        offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), 32);
        offset += 2;
        RandomNumberGenerator.Fill(buffer.AsSpan(offset, 32));
        offset += 32;

        var listLength = offset - listLengthPos - 2;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(listLengthPos, 2), (ushort)listLength);

        var extLength = offset - extLengthPos - 2;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(extLengthPos, 2), (ushort)extLength);
    }

    private static void WriteSupportedVersions(byte[] buffer, ref int offset)
    {
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), 0x002b);
        offset += 2;

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), 7);
        offset += 2;

        buffer[offset++] = 6;
        var greaseVer = TlsGreaseHelper.GetRandomGrease();
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), greaseVer);
        offset += 2;

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), 0x0304);
        offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), 0x0303);
        offset += 2;
    }

    private static void WriteSignatureAlgorithms(byte[] buffer, ref int offset)
    {
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), 0x000d);
        offset += 2;

        var byteLen = s_safariSignatures.Length * 2;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), (ushort)(byteLen + 2));
        offset += 2;

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), (ushort)byteLen);
        offset += 2;

        foreach (var algo in s_safariSignatures)
        {
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), algo);
            offset += 2;
        }
    }
}
