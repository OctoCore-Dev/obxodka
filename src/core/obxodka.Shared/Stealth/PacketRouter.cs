namespace obxodka.Shared.Stealth;

public static class PacketRouter
{
    public const int MaxRays = 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void GetRays(ReadOnlySpan<byte> packet, int activeRays, out int primaryRay, out int secondaryRay)
    {
        secondaryRay = -1;
        var length = packet.Length;

        if (activeRays <= 1 || length < 20)
        {
            primaryRay = 0;
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
                primaryRay = 0;
                return;
            }

            var flagsAndOffset = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(6, 2));
            var fragOffset = flagsAndOffset & 0x1FFF;

            if (fragOffset > 0)
            {
                if (protocol is 1 or 17)
                {
                    primaryRay = 0;
                    secondaryRay = activeRays switch
                    {
                        >= 8 => 7,
                        >= 4 => 3,
                        >= 3 => 2,
                        >= 2 => 1,
                        _ => -1
                    };
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

                primaryRay = activeRays switch
                {
                    >= 8 => 1 + ((hash & 0x7FFFFFFF) % 6),
                    >= 4 => 1 + ((hash & 0x7FFFFFFF) % 2),
                    >= 2 => hash & 1,
                    _ => 0
                };
                secondaryRay = -1;
                return;
            }

            var srcPort = length >= ihl + 2 ? BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(ihl, 2)) : 0;
            var dstPort = length >= ihl + 4 ? BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(ihl + 2, 2)) : 0;

            if (protocol == 1 || (protocol == 17 && (srcPort == 53 || dstPort == 53)))
            {
                isIcmpOrDns = true;
            }
            else if (protocol == 17 && srcPort > 0 && dstPort > 0 && srcPort != 443 && dstPort != 443 && length <= 1400)
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
                primaryRay = 0;
                secondaryRay = -1;
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
            else if (nextHeader == 17 && srcPort > 0 && dstPort > 0 && srcPort != 443 && dstPort != 443 && length <= 1400)
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
                primaryRay = 0;
                secondaryRay = -1;
                return;
            }
        }

        var isLatencyCritical = isIcmpOrDns || isGamingUdp;
        if (isLatencyCritical)
        {
            primaryRay = 0;
            secondaryRay = activeRays switch
            {
                >= 8 => 7,
                >= 4 => 3,
                >= 3 => 2,
                >= 2 => 1,
                _ => -1
            };
        }
        else
        {
            if (activeRays >= 8)
            {
                primaryRay = 1 + ((hash & 0x7FFFFFFF) % 6);
                secondaryRay = -1;
            }
            else if (activeRays >= 4)
            {
                primaryRay = 1 + ((hash & 0x7FFFFFFF) % 2);
                secondaryRay = -1;
            }
            else if (activeRays >= 2)
            {
                primaryRay = hash & 1;
                secondaryRay = -1;
            }
            else
            {
                primaryRay = 0;
                secondaryRay = -1;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void GetRays(byte[] packetBuffer, int length, int activeRays, out int primaryRay, out int secondaryRay) =>
        GetRays(packetBuffer.AsSpan(0, length), activeRays, out primaryRay, out secondaryRay);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetRayIndex(ReadOnlySpan<byte> packet, int activeRays)
    {
        GetRays(packet, activeRays, out var primary, out _);
        return primary;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetRayIndex(byte[] packetBuffer, int length, int activeRays) =>
        GetRayIndex(packetBuffer.AsSpan(0, length), activeRays);
}
