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
    private static readonly byte[] s_noiseBuffer = GC.AllocateUninitializedArray<byte>(8192, pinned: true);

    //таблица базовых масок для быстрой simd маскировки
    private static readonly byte[] s_baseMask = GC.AllocateUninitializedArray<byte>(MaxFrameSize + 1, pinned: true);

    //инициализация буфера шума и таблицы масок
    static ObxodkaFraming()
    {
        RandomNumberGenerator.Fill(s_noiseBuffer);
        for (var i = 0; i < s_baseMask.Length; i++)
        {
            s_baseMask[i] = (byte)((i * 37) + ((i >> 3) ^ 0x9D));
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

        Span<byte> input = stackalloc byte[128];
        var formatted = $"OBX-V7:{thumbprint.Trim().ToUpperInvariant()}:{ray}:{sessionSeed:X8}";
        var written = Encoding.UTF8.GetBytes(formatted, input);
        Span<byte> digest = stackalloc byte[32];
        _ = SHA256.HashData(input[..written], digest);
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

        Span<byte> input = stackalloc byte[192];
        var formatted = $"OBX-V7-AUTH:{thumbprint.Trim().ToUpperInvariant()}:{ray}:{sessionSeed:X8}:{timestampUnixSeconds}:7F4A9C2E1B8D6035";
        var written = Encoding.UTF8.GetBytes(formatted, input);
        Span<byte> digest = stackalloc byte[32];
        _ = SHA256.HashData(input[..written], digest);
        return Convert.ToHexString(digest[..8]);
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
    public static bool TryDecodeHeader(ReadOnlySpan<byte> header, out int totalLen, out int payloadLen, out byte command, out byte salt, uint sessionKey = 0)
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
                    ? (payloadLength <= 160
                        ? GetRandomInt(Math.Min(available, 64), Math.Min(available, 280))
                        : payloadLength <= 600
                            ? GetRandomInt(Math.Min(available, 24), Math.Min(available, 140))
                            : GetRandomInt(8, Math.Min(available, 48)))
                    : (payloadLength <= 128
                        ? GetRandomInt(8, Math.Min(available, 64))
                        : payloadLength <= 600
                            ? GetRandomInt(8, Math.Min(available, 40))
                            : 0);
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
            throw new ArgumentOutOfRangeException(nameof(payload), $"Payload length {payload.Length} exceeds maximum payload size {MaxFramePayloadSize}");
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
        var computedTotal = HeaderSize + payload.Length + paddingLen;
        if (computedTotal > MaxFrameSize || payload.Length > MaxFramePayloadSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), $"Frame total size {computedTotal} exceeds maximum allowed size {MaxFrameSize}");
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
            var noiseOffset = GetRandomInt(0, s_noiseBuffer.Length - paddingLen);
            s_noiseBuffer.AsSpan(noiseOffset, paddingLen).CopyTo(destination.Slice(HeaderSize + payloadLen, paddingLen));
        }
    }
}
