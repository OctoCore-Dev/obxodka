namespace obxodka.Shared.Stealth;

//кодирование, декодирование и simd-маскировка фреймов протокола obx-v7
public static class ObxodkaFraming
{
    public const ushort TotalMask = 0x7E3A; //16 битная маска для общей длины кадра
    public const ushort PayloadMask = 0x4C1F; //16 битная маска для длины полезной нагрузки кадра
    public const byte CommandMask = 0xA5; //8 битная маска для команды кадра

    public const byte CmdData = 0x01; //пользовательский пакет данных
    public const byte CmdPing = 0x02; //системный пинг зонд
    public const byte CmdPong = 0x03; //ответ на пинг зонд
    public const byte CmdHandshake = 0x04; //запрос на установку защищенного канала (handshake)
    public const byte CmdHandshakeResponse = 0x05; //ответ на запрос установки защищенного канала (handshake)
    public const byte CmdDisconnect = 0x06; //команда разрыва защищенного канала
    public const byte CmdMtuSync = 0x07; //команда синхронизации mtu
    public const byte CmdMtuBurstRequest = 0x08; //запрос на передачу пакета burst
    public const byte CmdMtuBurstPayload = 0x09; //полезная нагрузка пакета burst
    public const byte CmdChaff = 0x0A; //команда для создания шума

    public const int HeaderSize = 6; //размер заголовка кадра в байтах
    public const int MaxFrameSize = 65535; //максимальный размер кадра в байтах
    public const int MaxFramePayloadSize = MaxFrameSize - HeaderSize; //65529 байт
    public const int EarlyStageWindowBytes = 16384; //размер начального окна сессии 16 кб для усиленного паддинга

    //буфер случайного шума для заполнения паддинга кадра
    private static readonly byte[] s_noiseBuffer = GC.AllocateUninitializedArray<byte>(8192, true);

    //таблица базовых масок для быстрой simd маскировки
    private static readonly byte[] s_baseMask = GC.AllocateUninitializedArray<byte>(MaxFrameSize + 1, true);

    //инициализация буфера шума и таблицы масок
    static ObxodkaFraming()
    {
        RandomNumberGenerator.Fill(s_noiseBuffer);
        for (var i = 0; i < s_baseMask.Length; i++)
        {
            s_baseMask[i] = (byte)(i * 37 + ((i >> 3) ^ 0x9D));
        }
    }

    //вычисление sha256 без аллокаций промежуточного буфера для коротких строк
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ComputeUtf8Sha256(string formatted, Span<byte> digest)
    {
        var maxBytes = Encoding.UTF8.GetMaxByteCount(formatted.Length);
        if (maxBytes <= 512)
        {
            Span<byte> input = stackalloc byte[512];
            var written = Encoding.UTF8.GetBytes(formatted, input);
            _ = SHA256.HashData(input[..written], digest);
            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            var written = Encoding.UTF8.GetBytes(formatted, rented);
            _ = SHA256.HashData(rented.AsSpan(0, written), digest);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    //вычисление сессионного ключа на основе отпечатка сертификата, индекса луча и соли
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint DeriveSessionKey(string? thumbprint, int ray, uint sessionSeed)
    {
        if (sessionSeed == 0 || string.IsNullOrWhiteSpace(thumbprint))
        {
            return 0u;
        }

        var formatted = $"OBX-V7:{thumbprint.Trim().ToUpperInvariant()}:{ray}:{sessionSeed:X8}";
        Span<byte> digest = stackalloc byte[32];
        ComputeUtf8Sha256(formatted, digest);
        var derived = BinaryPrimitives.ReadUInt32LittleEndian(digest);
        return derived == 0 ? 0xA57E4C1Fu : derived;
    }

    //вычисление 16-символьного криптографического доказательства подлинности хендшейка против replay-атак тспу
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ComputeHandshakeProof(string? thumbprint, int ray, uint sessionSeed, long timestampUnixSeconds)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return string.Empty;
        }

        var formatted =
            $"OBX-V7-AUTH:{thumbprint.Trim().ToUpperInvariant()}:{ray}:{sessionSeed:X8}:{timestampUnixSeconds}:7F4A9C2E1B8D6035";
        Span<byte> digest = stackalloc byte[32];
        ComputeUtf8Sha256(formatted, digest);
        return Convert.ToHexString(digest[..8]);
    }

    //подпись параметров хендшейка закрытым ключом ecdsa p-256 клиентского сертификата устройства
    public static string SignHandshakeEcdsa(
        ECDsa? ecdsa,
        string? thumbprint,
        int ray,
        uint sessionSeed,
        long timestampUnixSeconds)
    {
        if (ecdsa is null || string.IsNullOrWhiteSpace(thumbprint))
        {
            return string.Empty;
        }

        var formatted = $"OBX-V8-SIG:{thumbprint.Trim().ToUpperInvariant()}:{ray}:{sessionSeed:X8}:{timestampUnixSeconds}";
        Span<byte> msgBytes = stackalloc byte[256];
        var msgLen = Encoding.UTF8.GetBytes(formatted, msgBytes);
        Span<byte> sigBuf = stackalloc byte[128];
        return ecdsa.TrySignData(msgBytes[..msgLen], sigBuf, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation, out var sigLen)
            ? System.Buffers.Text.Base64Url.EncodeToString(sigBuf[..sigLen])
            : string.Empty;
    }

    //проверка подписи ecdsa p-256 параметров хендшейка по открытому ключу spki устройства
    public static bool VerifyHandshakeEcdsa(
        ReadOnlySpan<byte> spkiDer,
        string? thumbprint,
        int ray,
        uint sessionSeed,
        long timestampUnixSeconds,
        string? signatureBase64Url)
    {
        if (spkiDer.IsEmpty || string.IsNullOrWhiteSpace(thumbprint) || string.IsNullOrWhiteSpace(signatureBase64Url))
        {
            return false;
        }

        Span<byte> sigBytes = stackalloc byte[128];
        if (!System.Buffers.Text.Base64Url.TryDecodeFromChars(signatureBase64Url.AsSpan().Trim(), sigBytes, out var sigLen) ||
            sigLen is < 64 or > 132)
        {
            return false;
        }

        var formatted = $"OBX-V8-SIG:{thumbprint.Trim().ToUpperInvariant()}:{ray}:{sessionSeed:X8}:{timestampUnixSeconds}";
        Span<byte> msgBytes = stackalloc byte[256];
        var msgLen = Encoding.UTF8.GetBytes(formatted, msgBytes);

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(spkiDer, out _);
            return ecdsa.VerifyData(
                msgBytes[..msgLen],
                sigBytes[..sigLen],
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    //упаковка режима и хендшейка в маскированный заголовок cookie под стандартные маркеры cdn и аналитики
    public static string PackStealthCookieHeader(string mode, string rawHandshake, long timestampUnixSeconds)
    {
        var combined = $"{mode}|{rawHandshake}";
        var byteCount = Encoding.UTF8.GetByteCount(combined);
        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Encoding.UTF8.GetBytes(combined, rented);
            var nonce = (ushort)RandomNumberGenerator.GetInt32(1, 65536);
            var n0 = (byte)(nonce >> 8);
            var n1 = (byte)(nonce & 0xFF);

            for (var i = 0; i < written; i++)
            {
                rented[i] ^= (byte)(s_baseMask[i % s_baseMask.Length] ^ ((i & 1) == 0 ? n0 : n1));
            }

            var encoded = System.Buffers.Text.Base64Url.EncodeToString(rented.AsSpan(0, written));
            var gaId = RandomNumberGenerator.GetInt32(100000000, 999999999);
            return $"_cfuvid={nonce:x4}.{encoded}; _ga=GA1.1.{gaId}.{timestampUnixSeconds}";
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    //извлечение и демаскировка режима и хендшейка из стандартного заголовка cookie
    public static bool TryUnpackStealthCookieHeader(string? cookieHeader, out string mode, out string rawHandshake)
    {
        mode = string.Empty;
        rawHandshake = string.Empty;
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return false;
        }

        const string marker = "_cfuvid=";
        var idx = cookieHeader.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
        {
            return false;
        }

        var valueStart = idx + marker.Length;
        var semiIdx = cookieHeader.IndexOf(';', valueStart);
        var tokenSpan = (semiIdx >= 0 ? cookieHeader.AsSpan(valueStart, semiIdx - valueStart) : cookieHeader.AsSpan(valueStart)).Trim();

        var dotIdx = tokenSpan.IndexOf('.');
        if (dotIdx != 4 ||
            !ushort.TryParse(tokenSpan[..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var nonce))
        {
            return false;
        }

        var encodedPart = tokenSpan[5..];
        if (encodedPart.IsEmpty || encodedPart.Length > 3072)
        {
            return false;
        }

        var maxDecoded = encodedPart.Length;
        var rented = ArrayPool<byte>.Shared.Rent(maxDecoded);
        try
        {
            if (!System.Buffers.Text.Base64Url.TryDecodeFromChars(encodedPart, rented, out var decodedLen) || decodedLen < 4)
            {
                return false;
            }

            var n0 = (byte)(nonce >> 8);
            var n1 = (byte)(nonce & 0xFF);
            for (var i = 0; i < decodedLen; i++)
            {
                rented[i] ^= (byte)(s_baseMask[i % s_baseMask.Length] ^ ((i & 1) == 0 ? n0 : n1));
            }

            var unpacked = Encoding.UTF8.GetString(rented, 0, decodedLen);
            var sep = unpacked.IndexOf('|');
            if (sep <= 0 || sep >= unpacked.Length - 1)
            {
                return false;
            }

            mode = unpacked[..sep].Trim();
            rawHandshake = unpacked[(sep + 1)..].Trim();
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    //маскирование полезной нагрузки кадра с солью через аппаратные векторные инструкции
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MaskPayload(Span<byte> payload, byte salt)
    {
        var len = payload.Length;
        if (len <= 0)
        {
            return;
        }

        if (len > s_baseMask.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), $"Payload length {len} exceeds maximum mask size {s_baseMask.Length}");
        }

        ref var dstRef = ref MemoryMarshal.GetReference(payload);
        ref var maskRef = ref MemoryMarshal.GetArrayDataReference(s_baseMask);
        var i = 0;

        if (Vector256.IsHardwareAccelerated && len >= 32)
        {
            var vSalt = Vector256.Create(salt);
            for (; i <= len - 32; i += 32)
            {
                var vBase = Vector256.LoadUnsafe(ref Unsafe.Add(ref maskRef, i));
                var vData = Vector256.LoadUnsafe(ref Unsafe.Add(ref dstRef, i));
                (vData ^ (vBase + vSalt)).StoreUnsafe(ref Unsafe.Add(ref dstRef, i));
            }
        }

        if (Vector128.IsHardwareAccelerated && i <= len - 16)
        {
            var vSalt128 = Vector128.Create(salt);
            for (; i <= len - 16; i += 16)
            {
                var vBase = Vector128.LoadUnsafe(ref Unsafe.Add(ref maskRef, i));
                var vData = Vector128.LoadUnsafe(ref Unsafe.Add(ref dstRef, i));
                (vData ^ (vBase + vSalt128)).StoreUnsafe(ref Unsafe.Add(ref dstRef, i));
            }
        }

        for (; i < len; i++)
        {
            Unsafe.Add(ref dstRef, i) ^= (byte)(Unsafe.Add(ref maskRef, i) + salt);
        }
    }

    //снятие маски с полезной нагрузки кадра
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UnmaskPayload(Span<byte> payload, byte salt, uint sessionKey = 0) =>
        MaskPayload(payload, (byte)(salt ^ (byte)sessionKey));

    //чтение заголовка кадра и проверка длины, команды и соли
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryDecodeHeader(ReadOnlySpan<byte> header, out int totalLen, out int payloadLen, out byte command, out byte salt,
        uint sessionKey = 0)
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

        var kTotal = (ushort)(sessionKey & 0xFFFF);
        var kPayload = (ushort)(sessionKey >> 16);
        var kCmd = (byte)((sessionKey >> 8) ^ (sessionKey >> 24));

        var totalMask = (ushort)(TotalMask ^ kTotal ^ ((salt << 8) | salt));
        var payloadMask = (ushort)(PayloadMask ^ kPayload ^ ((salt << 8) | (salt ^ 0x5C)));
        var cmdMask = (byte)(CommandMask ^ kCmd ^ salt);

        var decTotal = (ushort)(rawTotal ^ totalMask);
        var decPayload = (ushort)(rawPayload ^ payloadMask);
        var decCmd = (byte)(rawCmd ^ cmdMask);

        if (decCmd is < CmdData or > CmdChaff || decTotal < HeaderSize || decPayload > decTotal - HeaderSize)
        {
            return false;
        }

        totalLen = decTotal;
        payloadLen = decPayload;
        command = decCmd;
        return true;
    }

    //безопасная генерация случайного числа без падения
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetRandomInt(int minInclusive, int maxExclusive) =>
        maxExclusive <= minInclusive
            ? minInclusive
            : RandomNumberGenerator.GetInt32(minInclusive, maxExclusive);

    //расчет размера паддинга для маскировки длины пакета от анализа dpi
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalculatePaddingLength(byte command, int payloadLength, int maxMtu = 1280, bool isEarlySession = false)
    {
        if (command == CmdChaff)
        {
            return GetRandomInt(180, Math.Min(Math.Max(220, maxMtu - HeaderSize), 640));
        }

        if (command is CmdPong or CmdMtuSync or CmdMtuBurstRequest or CmdMtuBurstPayload)
        {
            return 0;
        }

        if (command == CmdData)
        {
            if (payloadLength >= 1100)
            {
                return 0;
            }

            var available = maxMtu - (HeaderSize + payloadLength);
            return available <= 8
                ? 0
                : isEarlySession
                    ? payloadLength <= 160
                        ? GetRandomInt(Math.Min(available, 64), Math.Min(available, 280))
                        : payloadLength <= 600
                            ? GetRandomInt(Math.Min(available, 24), Math.Min(available, 140))
                            : GetRandomInt(8, Math.Min(available, 48))
                    : payloadLength <= 128
                        ? GetRandomInt(8, Math.Min(available, 64))
                        : payloadLength <= 600
                            ? GetRandomInt(8, Math.Min(available, 40))
                            : 0;
        }

        if (payloadLength <= 100)
        {
            return GetRandomInt(64, 220);
        }

        if (payloadLength <= 1000)
        {
            return GetRandomInt(24, 96);
        }

        var avail = maxMtu - (HeaderSize + payloadLength);
        return avail <= 0 ? 0 : GetRandomInt(0, Math.Min(avail, 16));
    }

    //кодирование сырого сетевого пакета в кадр протокола с арендой буфера из пула
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] Pack(byte command, ReadOnlySpan<byte> payload, out int totalLen, int maxMtu = 1280, uint sessionKey = 0)
    {
        if (payload.Length > MaxFramePayloadSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload),
                $"Payload length {payload.Length} exceeds maximum payload size {MaxFramePayloadSize}");
        }

        var paddingLen = CalculatePaddingLength(command, payload.Length, maxMtu);
        var computedTotal = HeaderSize + payload.Length + paddingLen;
        if (computedTotal > MaxFrameSize)
        {
            paddingLen = MaxFrameSize - HeaderSize - payload.Length;
            computedTotal = MaxFrameSize;
        }

        totalLen = computedTotal;
        var buffer = ArrayPool<byte>.Shared.Rent(totalLen);
        WriteFrame(buffer.AsSpan(0, totalLen), command, payload, paddingLen, sessionKey);
        return buffer;
    }

    //запись сетевого пакета в существующий буфер кадра без лишних аллокаций
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteFrame(Span<byte> destination, byte command, ReadOnlySpan<byte> payload, int paddingLen, uint sessionKey = 0)
    {
        if (paddingLen < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(paddingLen), "Padding length cannot be negative");
        }

        var computedTotal = HeaderSize + payload.Length + paddingLen;
        if (computedTotal > MaxFrameSize || payload.Length > MaxFramePayloadSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload),
                $"Frame total size {computedTotal} exceeds maximum allowed size {MaxFrameSize}");
        }

        if (destination.Length < computedTotal)
        {
            throw new ArgumentOutOfRangeException(nameof(destination),
                $"Destination length {destination.Length} is smaller than frame size {computedTotal}");
        }

        var salt = (byte)RandomNumberGenerator.GetInt32(0, 256);
        var totalLen = (ushort)computedTotal;
        var payloadLen = (ushort)payload.Length;

        var kTotal = (ushort)(sessionKey & 0xFFFF);
        var kPayload = (ushort)(sessionKey >> 16);
        var kCmd = (byte)((sessionKey >> 8) ^ (sessionKey >> 24));

        destination[0] = salt;
        var totalMask = (ushort)(TotalMask ^ kTotal ^ ((salt << 8) | salt));
        var payloadMask = (ushort)(PayloadMask ^ kPayload ^ ((salt << 8) | (salt ^ 0x5C)));
        var cmdMask = (byte)(CommandMask ^ kCmd ^ salt);

        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(1, 2), (ushort)(totalLen ^ totalMask));
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(3, 2), (ushort)(payloadLen ^ payloadMask));
        destination[5] = (byte)(command ^ cmdMask);

        if (payloadLen > 0)
        {
            var payloadDest = destination.Slice(HeaderSize, payloadLen);
            payload.CopyTo(payloadDest);
            MaskPayload(payloadDest, (byte)(salt ^ (byte)sessionKey));
        }

        if (paddingLen > 0)
        {
            var padDest = destination.Slice(HeaderSize + payloadLen, paddingLen);
            var remaining = paddingLen;
            var destOffset = 0;
            while (remaining > 0)
            {
                var chunk = Math.Min(remaining, s_noiseBuffer.Length);
                var noiseOffset = GetRandomInt(0, s_noiseBuffer.Length - chunk);
                s_noiseBuffer.AsSpan(noiseOffset, chunk).CopyTo(padDest.Slice(destOffset, chunk));
                destOffset += chunk;
                remaining -= chunk;
            }
        }
    }
}
