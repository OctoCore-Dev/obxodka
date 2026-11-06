namespace obxodka.Shared.Stealth;

public static class PacketRouter
{
    public const int MaxRays = 3;

    public const byte Ray0Mask = 0x01; //основной луч
    public const byte Ray1Mask = 0x02; //вторичный луч (также передача и дубликация пинга)
    public const byte Ray2Mask = 0x04; //луч инспектор для инспектирование активных лучей
    public const byte AllRaysMask = Ray0Mask | Ray1Mask | Ray2Mask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void GetRays(
        ReadOnlySpan<byte> packet,
        byte aliveMask,
        int preferredRay,
        out int primaryRay,
        out int secondaryRay)
    {
        secondaryRay = -1;
        var length = packet.Length;

        if (aliveMask == 0 || length < 20)
        {
            primaryRay = preferredRay is 0 or 1 ? preferredRay : 0;
            return;
        }

        var version = packet[0] >> 4;
        var isIcmpOrDns = false;
        var isGamingUdp = false;
        var hash = 17;

        if (version == 4)
        {
            var protocol = packet[9];
            var ihl = (packet[0] & 0x0F) * 4;
            if (ihl < 20 || length < ihl)
            {
                SelectRaysFromHealth(isLatencyCritical: false, aliveMask, preferredRay, hash: 0, out primaryRay, out secondaryRay);
                return;
            }

            var flagsAndOffset = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(6, 2));
            var fragOffset = flagsAndOffset & 0x1FFF;

            if (fragOffset > 0)
            {
                if (protocol is 1 or 17)
                {
                    SelectRaysFromHealth(isLatencyCritical: true, aliveMask, preferredRay, hash: 0, out primaryRay, out secondaryRay);
                    return;
                }

                var fragSrcIp = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(12, 4));
                var fragDstIp = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(16, 4));
                var id = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(4, 2));
                unchecked
                {
                    hash = (hash * 31) + fragSrcIp;
                    hash = (hash * 31) + fragDstIp;
                    hash = (hash * 31) + id;
                    hash = (hash * 31) + protocol;
                }

                SelectRaysFromHealth(isLatencyCritical: false, aliveMask, preferredRay, hash, out primaryRay, out secondaryRay);
                return;
            }

            var srcPort = length >= ihl + 2 ? BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(ihl, 2)) : 0;
            var dstPort = length >= ihl + 4 ? BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(ihl + 2, 2)) : 0;

            if (protocol == 1 || (protocol == 17 && (srcPort == 53 || dstPort == 53)))
            {
                isIcmpOrDns = true;
            }
            else if (protocol == 17 && srcPort > 0 && dstPort > 0 && srcPort != 443 && dstPort != 443 && length <= 1500)
            {
                isGamingUdp = true;
            }

            var srcIp = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(12, 4));
            var dstIp = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(16, 4));

            unchecked
            {
                hash = (hash * 31) + srcIp;
                hash = (hash * 31) + dstIp;
                hash = (hash * 31) + srcPort;
                hash = (hash * 31) + dstPort;
                hash = (hash * 31) + protocol;
            }

            if (protocol == 17 && !isIcmpOrDns && !isGamingUdp)
            {
                SelectRaysFromHealth(isLatencyCritical: false, aliveMask, preferredRay, hash: 0, out primaryRay, out secondaryRay);
                return;
            }
        }
        else if (version == 6 && length >= 40)
        {
            var nextHeader = packet[6];
            var srcPort = length >= 42 ? BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(40, 2)) : 0;
            var dstPort = length >= 44 ? BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(42, 2)) : 0;

            if (nextHeader == 58 || (nextHeader == 17 && (srcPort == 53 || dstPort == 53)))
            {
                isIcmpOrDns = true;
            }
            else if (nextHeader == 17 && srcPort > 0 && dstPort > 0 && srcPort != 443 && dstPort != 443 && length <= 1500)
            {
                isGamingUdp = true;
            }

            var srcIp = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(20, 4));
            var dstIp = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(36, 4));

            unchecked
            {
                hash = (hash * 31) + srcIp;
                hash = (hash * 31) + dstIp;
                hash = (hash * 31) + srcPort;
                hash = (hash * 31) + dstPort;
                hash = (hash * 31) + nextHeader;
            }

            if (nextHeader == 17 && !isIcmpOrDns && !isGamingUdp)
            {
                SelectRaysFromHealth(isLatencyCritical: false, aliveMask, preferredRay, hash: 0, out primaryRay, out secondaryRay);
                return;
            }
        }

        var isLatencyCritical = isIcmpOrDns || isGamingUdp;
        SelectRaysFromHealth(isLatencyCritical, aliveMask, preferredRay, hash, out primaryRay, out secondaryRay);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SelectRaysFromHealth(
        bool isLatencyCritical,
        byte aliveMask,
        int preferredRay,
        int hash,
        out int primaryRay,
        out int secondaryRay)
    {
        var r0 = (aliveMask & Ray0Mask) != 0; //жив ли основной рабочий ray 0
        var r1 = (aliveMask & Ray1Mask) != 0; //жив ли вторичный рабочий ray 1
        //ray 2 инспектор это служебный канал зондирования control plane
        //он тестирует порты и меряет rtt, пользовательские пакеты идут только через ray 0 и ray 1

        if (isLatencyCritical)
        {
            //критичные данные игры dns пинг основной путь в живой рабочий луч
            primaryRay = (preferredRay is 0 or 1 && (aliveMask & (1 << preferredRay)) != 0)
                ? preferredRay
                : (r0 ? 0 : (r1 ? 1 : 0));

            //дублирование hedging строго между рабочими лучами ray 0 и ray 1
            //если летит по ray 0 а ray 1 жив дублируем в ray 1
            //если летит по ray 1 а ray 0 жив дублируем в ray 0
            //если жив только один рабочий луч дублировать некуда
            secondaryRay = (primaryRay == 0 && r1)
                ? 1
                : (primaryRay == 1 && r0)
                    ? 0
                    : -1;
        }
        else
        {
            //тяжелый трафик tcp видео дублирование выключено
            secondaryRay = -1;

            //распределяем строго между рабочими лучами ray 0 и ray 1
            primaryRay = (r0, r1) switch
            {
                (true, true) => hash & 1,        //оба живы: балансировка 50/50 по 5-tuple хэшу
                (true, false) => 0,              //ray 1 упал: 100% в ray 0
                (false, true) => 1,              //ray 0 упал: 100% в ray 1
                _ => preferredRay is 0 or 1 ? preferredRay : 0
            };
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void GetRays(ReadOnlySpan<byte> packet, int activeRays, out int primaryRay, out int secondaryRay)
    {
        //пользовательский трафик распределяется между рабочими лучами
        var clamped = Math.Clamp(activeRays, 1, 2);
        var mask = (byte)((1 << clamped) - 1);
        GetRays(packet, mask, preferredRay: -1, out primaryRay, out secondaryRay);
    }
}
