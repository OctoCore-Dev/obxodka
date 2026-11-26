namespace obxodka.Shared.Stealth;

//хирургический анализатор и морфер вложенных рукопожатий tls (0x16) против нейросетевого детекта tls-in-tls на тспу
public static class InnerTlsMorpher
{
    //верхний предел морфированного кадра поверх tcp/tls потока (разрушает привязку 1-к-1 к внутреннему mtu)
    public const int MaxMorphedFrameBytes = 1720;

    //сверхбыстрая проверка (o(1) без аллокаций) несет ли l3 ip-пакет вложенное рукопожатие tls (0x16 0x03) или tcp syn
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsInnerTlsControlOrHandshake(ReadOnlySpan<byte> l3Packet)
    {
        if (l3Packet.Length < 40)
        {
            return false;
        }

        var version = l3Packet[0] >> 4;
        int tcpOffset;

        if (version == 4)
        {
            if (l3Packet[9] != 6)
            {
                return false;
            }

            var ihl = (l3Packet[0] & 0x0F) * 4;
            if (ihl < 20 || l3Packet.Length < ihl + 20)
            {
                return false;
            }

            //пропускаем ненулевые фрагменты ipv4
            var fragField = BinaryPrimitives.ReadUInt16BigEndian(l3Packet.Slice(6, 2));
            if ((fragField & 0x1FFF) != 0)
            {
                return false;
            }

            tcpOffset = ihl;
        }
        else if (version == 6)
        {
            if (l3Packet.Length < 60 || l3Packet[6] != 6)
            {
                return false;
            }

            tcpOffset = 40;
        }
        else
        {
            return false;
        }

        var tcpFlags = l3Packet[tcpOffset + 13];
        //пакеты установки соединения syn / syn-ack также маскируются по длине
        if ((tcpFlags & 0x02) != 0)
        {
            return true;
        }

        var tcpHeaderLen = ((l3Packet[tcpOffset + 12] >> 4) & 0x0F) * 4;
        var payloadOffset = tcpOffset + tcpHeaderLen;
        if (tcpHeaderLen < 20 || payloadOffset + 6 > l3Packet.Length)
        {
            return false;
        }

        var tlsSpan = l3Packet[payloadOffset..];
        //0x16 = tls handshake (clienthello / serverhello / cert), 0x14 = change_cipher_spec
        return tlsSpan[0] is 0x16 or 0x14 &&
               tlsSpan[1] == 0x03 &&
               tlsSpan[2] <= 0x04;
    }

    //расчет нелинейного бакетизированного паддинга для разрушения корреляции размеров вложенного tls хендшейка
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalculateAntiMlPadding(ReadOnlySpan<byte> l3Payload, byte command, int maxMtu, bool isEarlySession)
    {
        if (command != ObxodkaFraming.CmdData || l3Payload.IsEmpty)
        {
            return ObxodkaFraming.CalculatePaddingLength(command, l3Payload.Length, maxMtu, isEarlySession);
        }

        if (!IsInnerTlsControlOrHandshake(l3Payload))
        {
            return ObxodkaFraming.CalculatePaddingLength(command, l3Payload.Length, maxMtu, isEarlySession);
        }

        //для вложенных рукопожатий tls (включая крупные post-quantum clienthello >1100б и цепочки сертификатов)
        //квантуем размер в нерегулярные веб-корзины и уводим за пределы константного сдвига +6 байт
        var rawLen = ObxodkaFraming.HeaderSize + l3Payload.Length;
        int targetMin;
        int targetMax;

        if (rawLen < 160)
        {
            targetMin = 240;
            targetMax = 460;
        }
        else if (rawLen < 520)
        {
            targetMin = 560;
            targetMax = 840;
        }
        else if (rawLen < 980)
        {
            targetMin = 1020;
            targetMax = 1340;
        }
        else
        {
            targetMin = rawLen + 68;
            targetMax = Math.Max(targetMin + 96, Math.Min(MaxMorphedFrameBytes, rawLen + 320));
        }

        var chosenTotal = RandomNumberGenerator.GetInt32(targetMin, targetMax + 1);
        var pad = chosenTotal - rawLen;
        return Math.Clamp(pad, 24, ObxodkaFraming.MaxFramePayloadSize - l3Payload.Length);
    }

    //вычисление точки неравномерного разреза буфера записи для расщепления внешних записей tls при вложенном хендшейке
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeIrregularRecordSplit(int totalBytes)
    {
        if (totalBytes < 320)
        {
            return 0;
        }

        var low = Math.Max(96, totalBytes / 5);
        var high = Math.Min(totalBytes - 96, (totalBytes * 3) / 5);
        return high > low ? RandomNumberGenerator.GetInt32(low, high) : 0;
    }
}
