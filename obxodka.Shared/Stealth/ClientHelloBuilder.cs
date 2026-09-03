namespace obxodka.Stealth;

public static class ClientHelloBuilder
{
    private static readonly Random t_random = Random.Shared;

    public static byte[] BuildChrome120ClientHello(
        ReadOnlySpan<byte> sni,
        ReadOnlySpan<byte> sessionId = default,
        ReadOnlySpan<byte> pskBinder = default)
    {
        var buffer = new byte[1024];
        var offset = 0;

        buffer[offset++] = 0x01;

        var lengthPos = offset;
        offset += 3;

        buffer[offset++] = 0x03;
        buffer[offset++] = 0x03;

        var random = new byte[32];
        t_random.NextBytes(random);
        random.CopyTo(buffer.AsSpan(offset));
        offset += 32;

        if (sessionId.Length > 0)
        {
            buffer[offset++] = (byte)sessionId.Length;
            sessionId.CopyTo(buffer.AsSpan(offset));
            offset += sessionId.Length;
        }
        else
        {
            buffer[offset++] = 0;
        }

        buffer[offset++] = 0x00;
        buffer[offset++] = 0x02;
        buffer[offset++] = 0x13;
        buffer[offset++] = 0x01;

        buffer[offset++] = 0x01;
        buffer[offset++] = 0x00;

        var extStart = offset;
        offset += 2;

        WriteExtensions(buffer, ref offset, sni, pskBinder);

        var extLength = offset - extStart - 2;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(extStart, 2), (ushort)extLength);

        var handshakeLength = offset - lengthPos - 3;
        buffer[lengthPos] = (byte)(handshakeLength >> 16);
        buffer[lengthPos + 1] = (byte)(handshakeLength >> 8);
        buffer[lengthPos + 2] = (byte)handshakeLength;

        var record = new byte[offset + 5];
        record[0] = 0x16;
        record[1] = 0x03;
        record[2] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(3, 2), (ushort)offset);
        buffer.AsSpan(0, offset).CopyTo(record.AsSpan(5));

        return record.AsSpan(0, offset + 5).ToArray();
    }

    private static void WriteExtensions(byte[] buffer, ref int offset, ReadOnlySpan<byte> sni, ReadOnlySpan<byte> pskBinder)
    {
        var extStart = offset;
        offset += 2;

        WriteServerName(buffer, ref offset, sni);
        WriteSupportedVersions(buffer, ref offset);
        WriteSupportedGroups(buffer, ref offset);
        WriteKeyShare(buffer, ref offset);
        WriteSignatureAlgorithms(buffer, ref offset);
        WriteSignatureAlgorithmsCert(buffer, ref offset);
        WritePskKeyExchangeModes(buffer, ref offset);
        WritePreSharedKey(buffer, ref offset, pskBinder);
        WriteCookie(buffer, ref offset);
        WriteRecordSizeLimit(buffer, ref offset);
        WriteApplicationSettings(buffer, ref offset);
        WriteCompressCertificate(buffer, ref offset);

        var extLength = offset - extStart - 2;
        BinaryPrimitives.WriteUInt16BigEndian(new Span<byte>(buffer, extStart, 2), (ushort)extLength);
    }

    private static void WriteServerName(byte[] buffer, ref int offset, ReadOnlySpan<byte> sni)
    {
        var start = offset;
        buffer[offset++] = 0x00;
        buffer[offset++] = 0x00;
        offset += 2;

        var listStart = offset;
        offset += 2;
        buffer[offset++] = 0x00;
        buffer[offset++] = (byte)(sni.Length >> 8);
        buffer[offset++] = (byte)sni.Length;
        sni.CopyTo(new Span<byte>(buffer, offset, sni.Length));
        offset += sni.Length;

        var listLength = offset - listStart - 2;
        BinaryPrimitives.WriteUInt16BigEndian(new Span<byte>(buffer, listStart, 2), (ushort)listLength);
        BinaryPrimitives.WriteUInt16BigEndian(new Span<byte>(buffer, start + 2, 2), (ushort)(listLength + 2));
    }

    private static void WriteSupportedVersions(byte[] buffer, ref int offset)
    {
        var start = offset;
        buffer[offset++] = 0x00;
        buffer[offset++] = 0x2b;
        offset += 2;

        buffer[offset++] = 0x04;
        buffer[offset++] = 0x03;
        buffer[offset++] = 0x04;
        buffer[offset++] = 0x03;
        buffer[offset++] = 0x03;

        var extLength = offset - start - 4;
        BinaryPrimitives.WriteUInt16BigEndian(new Span<byte>(buffer, start + 2, 2), (ushort)extLength);
    }

    private static void WriteSupportedGroups(byte[] buffer, ref int offset)
    {
        var start = offset;
        buffer[offset++] = 0x00;
        buffer[offset++] = 0x0a;
        offset += 2;

        var groups = new ushort[] { 0x001d, 0x0017, 0x0018, 0x0019, 0x0100, 0x0101, 0x0102 };
        buffer[offset++] = 0;
        buffer[offset++] = (byte)(groups.Length * 2);
        foreach (var g in groups)
        {
            buffer[offset++] = (byte)(g >> 8);
            buffer[offset++] = (byte)g;
        }

        BinaryPrimitives.WriteUInt16BigEndian(new Span<byte>(buffer, start + 2, 2), (ushort)(2 + (groups.Length * 2)));
    }

    private static void WriteKeyShare(byte[] buffer, ref int offset)
    {
        var start = offset;
        buffer[offset++] = 0x00;
        buffer[offset++] = 0x33;
        offset += 2;

        var listStart = offset;
        offset += 2;

        var group = 0x0017;
        var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        var rawPublicKey = publicKey.AsSpan(26);

        buffer[offset++] = (byte)(group >> 8);
        buffer[offset++] = (byte)group;
        BinaryPrimitives.WriteUInt16BigEndian(new Span<byte>(buffer, offset, 2), (ushort)rawPublicKey.Length);
        offset += 2;
        rawPublicKey.CopyTo(new Span<byte>(buffer, offset, rawPublicKey.Length));
        offset += rawPublicKey.Length;

        var entryLength = offset - listStart - 2;
        BinaryPrimitives.WriteUInt16BigEndian(new Span<byte>(buffer, listStart, 2), (ushort)entryLength);
        BinaryPrimitives.WriteUInt16BigEndian(new Span<byte>(buffer, start + 2, 2), (ushort)(entryLength + 2));
    }

    private static void WriteSignatureAlgorithms(byte[] buffer, ref int offset)
    {
        var start = offset;
        buffer[offset++] = 0x00;
        buffer[offset++] = 0x0d;
        offset += 2;

        var algos = new ushort[]
        {
            0x0403, 0x0503, 0x0603, 0x0807, 0x0808, 0x0809, 0x080a, 0x080b, 0x0804, 0x0805, 0x0806
        };
        buffer[offset++] = 0;
        buffer[offset++] = (byte)(algos.Length * 2);
        foreach (var a in algos)
        {
            buffer[offset++] = (byte)(a >> 8);
            buffer[offset++] = (byte)a;
        }

        BinaryPrimitives.WriteUInt16BigEndian(new Span<byte>(buffer, start + 2, 2), (ushort)(2 + (algos.Length * 2)));
    }

    private static void WriteSignatureAlgorithmsCert(byte[] buffer, ref int offset)
    {
        var start = offset;
        buffer[offset++] = 0x00;
        buffer[offset++] = 0x32;
        offset += 2;

        var algos = new ushort[] { 0x0403, 0x0503, 0x0603, 0x0807, 0x0808, 0x0809, 0x080a, 0x080b, 0x0804, 0x0805, 0x0806 };
        buffer[offset++] = 0;
        buffer[offset++] = (byte)(algos.Length * 2);
        foreach (var a in algos)
        {
            buffer[offset++] = (byte)(a >> 8);
            buffer[offset++] = (byte)a;
        }

        BinaryPrimitives.WriteUInt16BigEndian(new Span<byte>(buffer, start + 2, 2), (ushort)(2 + (algos.Length * 2)));
    }

    private static void WritePskKeyExchangeModes(byte[] buffer, ref int offset)
    {
        var start = offset;
        buffer[offset++] = 0x00;
        buffer[offset++] = 0x2d;
        offset += 2;

        buffer[offset++] = 0x01;
        buffer[offset++] = 0x01;

        BinaryPrimitives.WriteUInt16BigEndian(new Span<byte>(buffer, start + 2, 2), 2);
    }

    private static void WritePreSharedKey(byte[] buffer, ref int offset, ReadOnlySpan<byte> pskBinder)
    {
        var start = offset;
        buffer[offset++] = 0x00;
        buffer[offset++] = 0x29;
        offset += 2;

        offset += 2;

        buffer[offset++] = 0;
        buffer[offset++] = 0;
        buffer[offset++] = 0;
        buffer[offset++] = 0;

        var bindersStart = offset;
        offset += 2;

        if (pskBinder.Length > 0)
        {
            buffer[offset++] = (byte)pskBinder.Length;
            pskBinder.CopyTo(new Span<byte>(buffer, offset, pskBinder.Length));
            offset += pskBinder.Length;
        }
        else
        {
            buffer[offset++] = 0;
        }

        var binderLength = offset - bindersStart - 2;
        BinaryPrimitives.WriteUInt16BigEndian(new Span<byte>(buffer, bindersStart, 2), (ushort)binderLength);
        BinaryPrimitives.WriteUInt16BigEndian(new Span<byte>(buffer, start + 2, 2), (ushort)(offset - start - 4));
    }

    private static void WriteCookie(byte[] buffer, ref int offset)
    {
        var start = offset;
        buffer[offset++] = 0x00;
        buffer[offset++] = 0x2e;
        offset += 2;

        buffer[offset++] = 0;
        buffer[offset++] = 0;

        BinaryPrimitives.WriteUInt16BigEndian(new Span<byte>(buffer, start + 2, 2), 2);
    }

    private static void WriteRecordSizeLimit(byte[] buffer, ref int offset)
    {
        var start = offset;
        buffer[offset++] = 0x00;
        buffer[offset++] = 0x1c;
        offset += 2;

        unchecked
        {
            buffer[offset++] = 16384 >> 8;
            buffer[offset++] = (byte)16384;
        }

        BinaryPrimitives.WriteUInt16BigEndian(new Span<byte>(buffer, start + 2, 2), 2);
    }

    private static void WriteApplicationSettings(byte[] buffer, ref int offset)
    {
        var start = offset;
        buffer[offset++] = 0x00;
        buffer[offset++] = 0x2c;
        offset += 2;

        buffer[offset++] = 0;
        buffer[offset++] = 0;

        BinaryPrimitives.WriteUInt16BigEndian(new Span<byte>(buffer, start + 2, 2), 2);
    }

    private static void WriteCompressCertificate(byte[] buffer, ref int offset)
    {
        var start = offset;
        buffer[offset++] = 0x00;
        buffer[offset++] = 0x1b;
        offset += 2;

        buffer[offset++] = 0x01;
        buffer[offset++] = 0x01;

        BinaryPrimitives.WriteUInt16BigEndian(new Span<byte>(buffer, start + 2, 2), 2);
    }
}

