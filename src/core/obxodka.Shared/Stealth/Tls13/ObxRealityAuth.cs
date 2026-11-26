namespace obxodka.Shared.Stealth.Tls13;

//криптографическая аутентификация reality внутри 32-байтного поля session_id пакета tls 1.3 clienthello (неотличима от случайных байт для тспу)
public static class ObxRealityAuth
{
    private const uint ProtocolMagic = 0x4F425838u; // OBX8
    private const long AllowedClockSkewSeconds = 180;

    //детерминированная деривация x25519 ключей reality узла из существующего отпечатка spki сервера (без ручной перенастройки)
    public static void DeriveNodeRealityKeyPair(string spkiHashBase64, Span<byte> privateKey32, Span<byte> publicKey32)
    {
        var normalized = (spkiHashBase64 ?? string.Empty).Trim();
        var material = Encoding.UTF8.GetBytes($"OBX-V8-REALITY-NODE-KEY:{normalized}");
        Span<byte> digest = stackalloc byte[32];
        _ = SHA256.HashData(material, digest);
        digest.CopyTo(privateKey32[..32]);
        Curve25519.ClampScalar(privateKey32[..32]);
        Curve25519.GetPublicKey(privateKey32[..32], publicKey32[..32]);
    }

    //получение только публичного ключа reality узла по его spki пину
    public static byte[] DeriveNodeRealityPublicKey(string spkiHashBase64)
    {
        Span<byte> priv = stackalloc byte[32];
        var pub = new byte[32];
        DeriveNodeRealityKeyPair(spkiHashBase64, priv, pub);
        CryptographicOperations.ZeroMemory(priv);
        return pub;
    }

    //формирование 32-байтного поля legacy_session_id с зашифрованной меткой времени и тегом hmac-sha256 поверх ecdh x25519
    public static void BuildClientSessionId(
        ReadOnlySpan<byte> clientEphemeralPriv32,
        ReadOnlySpan<byte> serverRealityPub32,
        ReadOnlySpan<byte> clientRandom32,
        long unixSeconds,
        Span<byte> sessionId32)
    {
        if (sessionId32.Length < 32)
        {
            throw new ArgumentException("SessionId span must be 32 bytes.");
        }

        Span<byte> shared = stackalloc byte[32];
        if (!Curve25519.ComputeSharedSecret(clientEphemeralPriv32, serverRealityPub32, shared))
        {
            throw new CryptographicException("Invalid X25519 Reality peer public key.");
        }

        try
        {
            RandomNumberGenerator.Fill(sessionId32[..8]);

            Span<byte> maskInput = stackalloc byte[72];
            shared.CopyTo(maskInput[..32]);
            sessionId32[..8].CopyTo(maskInput.Slice(32, 8));
            clientRandom32[..32].CopyTo(maskInput.Slice(40, 32));

            Span<byte> keystream = stackalloc byte[32];
            _ = SHA256.HashData(maskInput, keystream);

            Span<byte> plainMeta = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(plainMeta[..4], (uint)unixSeconds);
            BinaryPrimitives.WriteUInt32BigEndian(plainMeta.Slice(4, 4), ProtocolMagic);

            for (var i = 0; i < 8; i++)
            {
                sessionId32[8 + i] = (byte)(plainMeta[i] ^ keystream[i]);
            }

            Span<byte> macData = stackalloc byte[48];
            sessionId32[..16].CopyTo(macData[..16]);
            clientRandom32[..32].CopyTo(macData.Slice(16, 32));

            Span<byte> macTag = stackalloc byte[32];
            _ = HMACSHA256.HashData(shared, macData, macTag);
            macTag[..16].CopyTo(sessionId32.Slice(16, 16));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    //проверка сырого пакета clienthello до терминации tls: извлекает x25519 key_share, sni и валидирует тег reality
    public static bool TryVerifyClientHello(
        ReadOnlySpan<byte> tlsRecord,
        ReadOnlySpan<byte> serverRealityPriv32,
        long nowUnixSeconds,
        out byte[] clientHandshakeMsg,
        out byte[] clientRandom,
        out byte[] sessionId,
        out byte[] clientX25519Pub,
        out string sniHost,
        out ulong replayFingerprint)
    {
        clientHandshakeMsg = [];
        clientRandom = [];
        sessionId = [];
        clientX25519Pub = [];
        sniHost = string.Empty;
        replayFingerprint = 0;

        //проверка заголовка tls record (0x16 = handshake)
        if (tlsRecord.Length < 48 || tlsRecord[0] != 0x16 || tlsRecord[1] != 0x03)
        {
            return false;
        }

        //сборка всех записей handshake если клиент применил tls record fragmentation (dpibypassstream)
        if (!TryReassembleHandshakeMessage(tlsRecord, out var assembledHandshake))
        {
            return false;
        }

        ReadOnlySpan<byte> hs = assembledHandshake;
        if (hs.Length < 42 || hs[0] != 0x01)
        {
            return false;
        }

        var hsBodyLen = (hs[1] << 16) | (hs[2] << 8) | hs[3];
        if (hsBodyLen <= 38 || hs.Length < 4 + hsBodyLen)
        {
            return false;
        }

        var msgSpan = hs[..(4 + hsBodyLen)];
        var offset = 4;

        //legacy_version (2b) + client_random (32b)
        offset += 2;
        var rndSpan = msgSpan.Slice(offset, 32);
        offset += 32;

        var sidLen = msgSpan[offset++];
        if (sidLen != 32 || offset + 32 > msgSpan.Length)
        {
            return false;
        }

        var sidSpan = msgSpan.Slice(offset, 32);
        offset += 32;

        if (offset + 2 > msgSpan.Length)
        {
            return false;
        }

        var ciphersLen = BinaryPrimitives.ReadUInt16BigEndian(msgSpan.Slice(offset, 2));
        offset += 2 + ciphersLen;
        if (offset + 1 > msgSpan.Length)
        {
            return false;
        }

        var compLen = msgSpan[offset++];
        offset += compLen;
        if (offset + 2 > msgSpan.Length)
        {
            return false;
        }

        var extTotalLen = BinaryPrimitives.ReadUInt16BigEndian(msgSpan.Slice(offset, 2));
        offset += 2;
        var extEnd = Math.Min(msgSpan.Length, offset + extTotalLen);

        ReadOnlySpan<byte> foundX25519 = default;
        var foundSni = string.Empty;

        while (offset + 4 <= extEnd)
        {
            var extType = BinaryPrimitives.ReadUInt16BigEndian(msgSpan.Slice(offset, 2));
            var extLen = BinaryPrimitives.ReadUInt16BigEndian(msgSpan.Slice(offset + 2, 2));
            offset += 4;
            if (offset + extLen > extEnd)
            {
                return false;
            }

            var extBody = msgSpan.Slice(offset, extLen);
            if (extType == 0x0000 && extBody.Length >= 5 && extBody[2] == 0x00)
            {
                var nameLen = BinaryPrimitives.ReadUInt16BigEndian(extBody.Slice(3, 2));
                if (5 + nameLen <= extBody.Length)
                {
                    foundSni = Encoding.ASCII.GetString(extBody.Slice(5, nameLen));
                }
            }
            else if (extType == 0x0033 && extBody.Length >= 6)
            {
                var sharesLen = BinaryPrimitives.ReadUInt16BigEndian(extBody[..2]);
                var sPos = 2;
                var sEnd = Math.Min(extBody.Length, 2 + sharesLen);
                while (sPos + 4 <= sEnd)
                {
                    var group = BinaryPrimitives.ReadUInt16BigEndian(extBody.Slice(sPos, 2));
                    var kLen = BinaryPrimitives.ReadUInt16BigEndian(extBody.Slice(sPos + 2, 2));
                    sPos += 4;
                    if (sPos + kLen > sEnd)
                    {
                        break;
                    }

                    if (group == 0x001d && kLen == 32)
                    {
                        foundX25519 = extBody.Slice(sPos, 32);
                        break;
                    }

                    //гибридный x25519mlkem768 (0x11ec): последние 32 байта — классический ключ x25519
                    if (group == 0x11ec && kLen == 1216)
                    {
                        foundX25519 = extBody.Slice(sPos + 1184, 32);
                    }

                    sPos += kLen;
                }
            }

            offset += extLen;
        }

        if (foundX25519.Length != 32)
        {
            return false;
        }

        Span<byte> shared = stackalloc byte[32];
        if (!Curve25519.ComputeSharedSecret(serverRealityPriv32, foundX25519, shared))
        {
            return false;
        }

        try
        {
            Span<byte> macData = stackalloc byte[48];
            sidSpan[..16].CopyTo(macData[..16]);
            rndSpan.CopyTo(macData.Slice(16, 32));

            Span<byte> expectedMac = stackalloc byte[32];
            _ = HMACSHA256.HashData(shared, macData, expectedMac);

            if (!CryptographicOperations.FixedTimeEquals(expectedMac[..16], sidSpan.Slice(16, 16)))
            {
                return false;
            }

            Span<byte> maskInput = stackalloc byte[72];
            shared.CopyTo(maskInput[..32]);
            sidSpan[..8].CopyTo(maskInput.Slice(32, 8));
            rndSpan.CopyTo(maskInput.Slice(40, 32));

            Span<byte> keystream = stackalloc byte[32];
            _ = SHA256.HashData(maskInput, keystream);

            Span<byte> plainMeta = stackalloc byte[8];
            for (var i = 0; i < 8; i++)
            {
                plainMeta[i] = (byte)(sidSpan[8 + i] ^ keystream[i]);
            }

            var magic = BinaryPrimitives.ReadUInt32BigEndian(plainMeta.Slice(4, 4));
            if (magic != ProtocolMagic)
            {
                return false;
            }

            var clientUnixSec = (long)BinaryPrimitives.ReadUInt32BigEndian(plainMeta[..4]);
            var now = nowUnixSeconds > 0 ? nowUnixSeconds : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Math.Abs(now - clientUnixSec) > AllowedClockSkewSeconds)
            {
                return false;
            }

            Span<byte> replayHash = stackalloc byte[32];
            _ = SHA256.HashData(macData, replayHash);
            replayFingerprint = BinaryPrimitives.ReadUInt64LittleEndian(replayHash);

            clientHandshakeMsg = msgSpan.ToArray();
            clientRandom = rndSpan.ToArray();
            sessionId = sidSpan.ToArray();
            clientX25519Pub = foundX25519.ToArray();
            sniHost = foundSni;
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    //сборка фрагментированных tls record записей (0x16) в единое сообщение clienthello
    private static bool TryReassembleHandshakeMessage(ReadOnlySpan<byte> rawBytes, out byte[] handshakeMsg)
    {
        handshakeMsg = [];
        var pos = 0;
        using var ms = new MemoryStream(rawBytes.Length);

        while (pos + 5 <= rawBytes.Length)
        {
            if (rawBytes[pos] != 0x16 || rawBytes[pos + 1] != 0x03)
            {
                break;
            }

            var recLen = BinaryPrimitives.ReadUInt16BigEndian(rawBytes.Slice(pos + 3, 2));
            if (recLen == 0 || pos + 5 + recLen > rawBytes.Length)
            {
                return false;
            }

            ms.Write(rawBytes.Slice(pos + 5, recLen));
            pos += 5 + recLen;

            if (ms.Length >= 4)
            {
                var buf = ms.GetBuffer();
                var expectedBodyLen = (buf[1] << 16) | (buf[2] << 8) | buf[3];
                if (ms.Length >= 4 + expectedBodyLen)
                {
                    handshakeMsg = ms.ToArray();
                    return true;
                }
            }
        }

        return false;
    }
}
