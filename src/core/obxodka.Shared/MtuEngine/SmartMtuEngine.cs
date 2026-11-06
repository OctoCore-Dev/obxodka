namespace obxodka.Shared.MtuEngine;

//ядро автоматической калибровки и адаптации mtu: mss clamping, pmtud icmp и фрагментация
public static class SmartMtuEngine
{
    public const int DefaultMtu = 1280;
    public const int MinMtu = 1200;
    public const int MaxMtu = 1500;

    //текущий рабочий размер mtu туннеля (потокобезопасный единый источник истины)
    public static int CurrentMtu
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref field);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => Volatile.Write(ref field, ClampMtu(value));
    } = DefaultMtu;

    //шаги эскалации для залповой проверки проходимости канала (burst probing)
    public static readonly int[] MtuEscalationSteps = [1200, 1240, 1280, 1300, 1320, 1360, 1400, 1420];

    //кандидаты размеров mtu для точного сканирования
    public static int[] MtuCandidates => GenerateMtuScanCandidates(MaxMtu, MinMtu, 8);

    //генерация сетки кандидатов mtu от максимума к минимуму с заданным шагом
    public static int[] GenerateMtuScanCandidates(int max = MaxMtu, int min = MinMtu, int step = 8)
    {
        var candidates = new List<int>();
        for (var m = max; m >= min; m -= step)
        {
            candidates.Add(m);
        }
        if (candidates.Count == 0 || candidates[^1] != min)
        {
            candidates.Add(min);
        }
        return [.. candidates];
    }

    //ограничение значения mtu допустимыми границами протокола
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ClampMtu(int mtu) => Math.Clamp(mtu, MinMtu, MaxMtu);

    //проверка и подрезка mss в tcp syn пакете под текущий рабочий mtu клиента
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ClampTcpMss(Span<byte> pkt, int clientMtu)
    {
        if (pkt.Length < 40)
        {
            return;
        }

        var version = pkt[0] >> 4;
        if (version == 4)
        {
            if (pkt[9] != 6)
            {
                return;
            }

            var ihl = (pkt[0] & 0x0F) * 4;
            if (ihl < 20 || pkt.Length < ihl + 24 || (pkt[ihl + 13] & 0x02) == 0)
            {
                return;
            }

            var tcpHeaderLen = ((pkt[ihl + 12] >> 4) & 0x0F) * 4;
            if (tcpHeaderLen <= 20 || pkt.Length < ihl + tcpHeaderLen)
            {
                return;
            }

            var maxMss = (ushort)Math.Clamp(clientMtu - 140, 536, 1360);
            if (TryPatchTcpOptionsMss(pkt.Slice(ihl + 20, tcpHeaderLen - 20), maxMss))
            {
                Ipv4MtuHandler.RecalculateTcpChecksum(pkt, ihl);
            }
        }
        else if (version == 6)
        {
            if (pkt.Length < 64 || pkt[6] != 6 || (pkt[40 + 13] & 0x02) == 0)
            {
                return;
            }

            var tcpHeaderLen = ((pkt[40 + 12] >> 4) & 0x0F) * 4;
            if (tcpHeaderLen <= 20 || pkt.Length < 40 + tcpHeaderLen)
            {
                return;
            }

            var maxMss = (ushort)Math.Clamp(clientMtu - 160, 536, 1340);
            if (TryPatchTcpOptionsMss(pkt.Slice(60, tcpHeaderLen - 20), maxMss))
            {
                Ipv6MtuHandler.RecalculateTcpChecksum(pkt);
            }
        }
    }

    //сканирование опций tcp заголовка и снижение mss при превышении порога
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryPatchTcpOptionsMss(Span<byte> options, ushort maxMss)
    {
        var idx = 0;
        while (idx < options.Length)
        {
            var kind = options[idx];
            if (kind == 0)
            {
                break;
            }
            if (kind == 1)
            {
                idx++;
                continue;
            }
            if (idx + 1 >= options.Length)
            {
                break;
            }
            var len = options[idx + 1];
            if (len < 2 || idx + len > options.Length)
            {
                break;
            }
            if (kind == 2 && len == 4)
            {
                var currentMss = BinaryPrimitives.ReadUInt16BigEndian(options.Slice(idx + 2, 2));
                if (currentMss > maxMss)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(options.Slice(idx + 2, 2), maxMss);
                    return true;
                }
                return false;
            }
            idx += len;
        }
        return false;
    }

    //нарезка крупного ipv4 пакета (df=0) на фрагменты под целевой mtu с арендой буферов из пула
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryFragmentIpv4(ReadOnlySpan<byte> packet, int length, int clientMtu, Action<byte[], int> onFragmentReady) =>
        Ipv4MtuHandler.TryFragment(packet, length, clientMtu, onFragmentReady);

    //формирование пакета icmp fragmentation needed (type 3, code 4) для аппаратного pmtud ipv4
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryBuildIcmpFragmentationNeeded(ReadOnlySpan<byte> packet, int length, int clientMtu, out byte[] icmpPacket, out int icmpLen) =>
        Ipv4MtuHandler.TryBuildIcmpFragmentationNeeded(packet, length, clientMtu, out icmpPacket, out icmpLen);

    //формирование пакета icmpv6 packet too big (type 2, code 0) для аппаратного pmtud ipv6
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryBuildIcmpPacketTooBig(ReadOnlySpan<byte> packet, int length, int clientMtu, out byte[] icmpPacket, out int icmpLen) =>
        Ipv6MtuHandler.TryBuildIcmpPacketTooBig(packet, length, clientMtu, out icmpPacket, out icmpLen);

    //формирование пакета icmpv6 packet too big (алиас для совместимости)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryBuildIcmpV6PacketTooBig(ReadOnlySpan<byte> packet, int length, int clientMtu, out byte[] icmpPacket, out int icmpLen) =>
        Ipv6MtuHandler.TryBuildIcmpPacketTooBig(packet, length, clientMtu, out icmpPacket, out icmpLen);

    //пересчет контрольной суммы заголовка ipv4
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecalculateIpv4HeaderChecksum(Span<byte> header) =>
        Ipv4MtuHandler.RecalculateHeaderChecksum(header);
}
