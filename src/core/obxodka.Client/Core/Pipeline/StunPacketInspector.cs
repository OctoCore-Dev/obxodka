namespace obxodka.Core.Pipeline;

//инспектор udp-пакетов и доменов stun/turn (rfc 5389) для предотвращения утечек webrtc мимо туннеля
public static class StunPacketInspector
{
    private const uint StunMagicCookie = 0x2112A442u;
    private const int MinStunHeaderLength = 20;

    //стандартные порты stun/turn (rfc 5389, turns, google webrtc)
    public static ReadOnlySpan<ushort> StandardStunPorts => [3478, 5349, 19302];

    //проверка стандартного порта stun/turn
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsStunPort(ushort port) => port is 3478 or 5349 or 19302;

    //проверка заголовка rfc 5389 stun magic cookie (0x2112a442 на смещении 4 байта полезной нагрузки udp)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasStunMagicCookie(ReadOnlySpan<byte> udpPayload) =>
        udpPayload.Length >= MinStunHeaderLength &&
        (udpPayload[0] & 0xC0) == 0 &&
        BinaryPrimitives.ReadUInt32BigEndian(udpPayload.Slice(4, 4)) == StunMagicCookie;

    //комплексная проверка udp-пакета по портам и сигнатуре rfc 5389
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsStunUdpPacket(ushort srcPort, ushort dstPort, ReadOnlySpan<byte> udpPayload) =>
        IsStunPort(dstPort) || IsStunPort(srcPort) || HasStunMagicCookie(udpPayload);

    //проверка сырого ipv4 udp-пакета на принадлежность к stun/turn
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsStunIpv4Packet(ReadOnlySpan<byte> ipPacket)
    {
        if (ipPacket.Length < 28 || (ipPacket[0] >> 4) != 4 || ipPacket[9] != 17)
        {
            return false;
        }

        var ihl = (ipPacket[0] & 0x0F) * 4;
        if (ihl < 20 || ipPacket.Length < ihl + 8)
        {
            return false;
        }

        var srcPort = BinaryPrimitives.ReadUInt16BigEndian(ipPacket.Slice(ihl, 2));
        var dstPort = BinaryPrimitives.ReadUInt16BigEndian(ipPacket.Slice(ihl + 2, 2));
        return IsStunUdpPacket(srcPort, dstPort, ipPacket[(ihl + 8)..]);
    }

    //проверка сырого ipv6 udp-пакета на принадлежность к stun/turn
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsStunIpv6Packet(ReadOnlySpan<byte> ipPacket)
    {
        if (ipPacket.Length < 48 || (ipPacket[0] >> 4) != 6 || ipPacket[6] != 17)
        {
            return false;
        }

        var srcPort = BinaryPrimitives.ReadUInt16BigEndian(ipPacket.Slice(40, 2));
        var dstPort = BinaryPrimitives.ReadUInt16BigEndian(ipPacket.Slice(42, 2));
        return IsStunUdpPacket(srcPort, dstPort, ipPacket[48..]);
    }

    //проверка сырого l3-пакета ipv4 или ipv6 на принадлежность к stun/turn
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsStunPacket(ReadOnlySpan<byte> ipPacket) =>
        ipPacket.Length >= 28 && (ipPacket[0] >> 4) switch
        {
            4 => IsStunIpv4Packet(ipPacket),
            6 => IsStunIpv6Packet(ipPacket),
            _ => false
        };

    //проверка имени хоста на принадлежность к stun/turn серверу (без ложных совпадений вроде saturn.ru)
    public static bool IsStunHostname(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return false;
        }

        var span = hostname.AsSpan().Trim().TrimEnd('.');
        while (!span.IsEmpty)
        {
            var dotIdx = span.IndexOf('.');
            var label = dotIdx >= 0 ? span[..dotIdx] : span;
            if (IsStunOrTurnLabel(label))
            {
                return true;
            }

            if (dotIdx < 0)
            {
                break;
            }

            span = span[(dotIdx + 1)..];
        }

        return false;
    }

    private static bool IsStunOrTurnLabel(ReadOnlySpan<char> label)
    {
        if (label.Length < 4)
        {
            return false;
        }

        if (MatchesServiceToken(label, "stun") || MatchesServiceToken(label, "turn"))
        {
            return true;
        }

        var dashIdx = label.IndexOf('-');
        while (dashIdx >= 0)
        {
            var part = label[..dashIdx];
            if (MatchesServiceToken(part, "stun") || MatchesServiceToken(part, "turn"))
            {
                return true;
            }

            label = label[(dashIdx + 1)..];
            dashIdx = label.IndexOf('-');
        }

        return MatchesServiceToken(label, "stun") || MatchesServiceToken(label, "turn");
    }

    private static bool MatchesServiceToken(ReadOnlySpan<char> token, ReadOnlySpan<char> prefix)
    {
        if (!token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (token.Length == prefix.Length)
        {
            return true;
        }

        //разрешаем только числовые суффиксы вроде stun1, stun02, turn3
        for (var i = prefix.Length; i < token.Length; i++)
        {
            if (!char.IsAsciiDigit(token[i]))
            {
                return false;
            }
        }

        return true;
    }
}
