using System.Buffers.Binary;
namespace obxodka.Shared.Stealth;

public static class PacketRouter
{
    public const int MaxRays = 8;
    public static int GetRayIndex(byte[] packetBuffer, int length)
    {
        if (length < 20)
        {
            return 0;
        }
        var version = packetBuffer[0] >> 4;
        var hash = 17;
        var isGamingOrIcmp = false;
        unchecked
        {
            if (version == 4 && length >= 20)
            {
                hash = (hash * 31) + BinaryPrimitives.ReadInt32LittleEndian(packetBuffer.AsSpan(12));
                hash = (hash * 31) + BinaryPrimitives.ReadInt32LittleEndian(packetBuffer.AsSpan(16));
                var protocol = packetBuffer[9];
                hash = (hash * 31) + protocol;
                if (protocol == 1)
                {
                    isGamingOrIcmp = true;
                }
                var ihl = (packetBuffer[0] & 0x0F) * 4;
                if (length >= ihl + 4 && (protocol == 6 || protocol == 17))
                {
                    var srcPort = BinaryPrimitives.ReadUInt16BigEndian(packetBuffer.AsSpan(ihl));
                    var dstPort = BinaryPrimitives.ReadUInt16BigEndian(packetBuffer.AsSpan(ihl + 2));
                    hash = (hash * 31) + srcPort;
                    hash = (hash * 31) + dstPort;
                    if (protocol == 17 && dstPort != 53 && dstPort != 443 && srcPort != 53 && srcPort != 443)
                    {
                        isGamingOrIcmp = true;
                    }
                }
            }
            else if (version == 6 && length >= 40)
            {
                hash = (hash * 31) + BinaryPrimitives.ReadInt32LittleEndian(packetBuffer.AsSpan(8));
                hash = (hash * 31) + BinaryPrimitives.ReadInt32LittleEndian(packetBuffer.AsSpan(24));
                var nextHeader = packetBuffer[6];
                hash = (hash * 31) + nextHeader;
                if (nextHeader == 58)
                {
                    isGamingOrIcmp = true;
                }
                if (length >= 44 && (nextHeader == 6 || nextHeader == 17))
                {
                    var srcPort = BinaryPrimitives.ReadUInt16BigEndian(packetBuffer.AsSpan(40));
                    var dstPort = BinaryPrimitives.ReadUInt16BigEndian(packetBuffer.AsSpan(42));
                    hash = (hash * 31) + srcPort;
                    hash = (hash * 31) + dstPort;
                    if (nextHeader == 17 && dstPort != 53 && dstPort != 443 && srcPort != 53 && srcPort != 443)
                    {
                        isGamingOrIcmp = true;
                    }
                }
            }
        }
        return isGamingOrIcmp ? 0 : (Math.Abs(hash) % (MaxRays - 1)) + 1;
    }
}
