namespace obxodka.Shared.Stealth;

public static class PacketRouter
{
    public const int MaxRays = 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void GetRays(byte[] packetBuffer, int length, int activeRays, out int primaryRay, out int secondaryRay)
    {
        secondaryRay = -1;

        if (activeRays <= 1 || length < 20)
        {
            primaryRay = 0;
            return;
        }

        var version = packetBuffer[0] >> 4;
        var isRealtimeGaming = false;
        var hash = 17;

        if (version == 4)
        {
            var protocol = packetBuffer[9];
            var ihl = (packetBuffer[0] & 0x0F) * 4;

            if (protocol is 1 or 17 && length <= 600)
            {
                isRealtimeGaming = true;
            }
            else if (protocol == 6 && length >= ihl + 20)
            {
                var tcpHeaderLen = (packetBuffer[ihl + 12] >> 4) * 4;
                if (length <= ihl + tcpHeaderLen)
                {
                    isRealtimeGaming = true;
                }
            }

            var srcIp = BinaryPrimitives.ReadInt32LittleEndian(packetBuffer.AsSpan(12, 4));
            var dstIp = BinaryPrimitives.ReadInt32LittleEndian(packetBuffer.AsSpan(16, 4));
            var srcPort = length >= ihl + 2 ? BinaryPrimitives.ReadUInt16BigEndian(packetBuffer.AsSpan(ihl, 2)) : 0;
            var dstPort = length >= ihl + 4 ? BinaryPrimitives.ReadUInt16BigEndian(packetBuffer.AsSpan(ihl + 2, 2)) : 0;

            unchecked
            {
                hash = (hash * 31) + srcIp;
                hash = (hash * 31) + dstIp;
                hash = (hash * 31) + srcPort;
                hash = (hash * 31) + dstPort;
            }
        }
        else if (version == 6 && length >= 40)
        {
            var nextHeader = packetBuffer[6];

            if (nextHeader is 58 or 17 && length <= 600)
            {
                isRealtimeGaming = true;
            }
            else if (nextHeader == 6 && length >= 60)
            {
                var tcpHeaderLen = (packetBuffer[40 + 12] >> 4) * 4;
                if (length <= 40 + tcpHeaderLen)
                {
                    isRealtimeGaming = true;
                }
            }

            var srcIp = BinaryPrimitives.ReadInt32LittleEndian(packetBuffer.AsSpan(20, 4));
            var dstIp = BinaryPrimitives.ReadInt32LittleEndian(packetBuffer.AsSpan(36, 4));
            var srcPort = length >= 42 ? BinaryPrimitives.ReadUInt16BigEndian(packetBuffer.AsSpan(40, 2)) : 0;
            var dstPort = length >= 44 ? BinaryPrimitives.ReadUInt16BigEndian(packetBuffer.AsSpan(42, 2)) : 0;

            unchecked
            {
                hash = (hash * 31) + srcIp;
                hash = (hash * 31) + dstIp;
                hash = (hash * 31) + srcPort;
                hash = (hash * 31) + dstPort;
            }
        }

        if (isRealtimeGaming)
        {
            primaryRay = 0;
            secondaryRay = activeRays >= 2 ? 1 : -1;
        }
        else
        {
            if (activeRays >= 3)
            {
                var bulkPool = activeRays - 2;
                primaryRay = 2 + (Math.Abs(hash) % bulkPool);
            }
            else
            {
                primaryRay = Math.Abs(hash) % activeRays;
            }
            secondaryRay = -1;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetRayIndex(byte[] packetBuffer, int length, int activeRays)
    {
        GetRays(packetBuffer, length, activeRays, out var primary, out _);
        return primary;
    }
}
