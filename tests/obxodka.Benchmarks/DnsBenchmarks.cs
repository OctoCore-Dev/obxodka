namespace obxodka.Benchmarks;

[MemoryDiagnoser]
public class DnsBenchmarks
{
    private byte[] _blockedPacket = null!;
    private byte[] _allowedPacket = null!;

    [GlobalSetup]
    public void Setup()
    {
        _blockedPacket = CreateDnsQueryPacket("pagead2.googlesyndication.com");
        _allowedPacket = CreateDnsQueryPacket("youtube.com");
    }

    private static byte[] CreateDnsQueryPacket(string domain)
    {
        var packet = new byte[20 + 8 + 12 + domain.Length + 2 + 4];
        packet[0] = 0x45;
        packet[9] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20, 2), 12345);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), 53);
        var dnsOffset = 28;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(dnsOffset + 4, 2), 1);
        var qnameOffset = dnsOffset + 12;
        var parts = domain.Split('.');
        var currentOffset = qnameOffset;
        foreach (var part in parts)
        {
            packet[currentOffset++] = (byte)part.Length;
            var partBytes = Encoding.UTF8.GetBytes(part);
            Buffer.BlockCopy(partBytes, 0, packet, currentOffset, partBytes.Length);
            currentOffset += partBytes.Length;
        }
        packet[currentOffset++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(currentOffset, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(currentOffset + 2, 2), 1);
        return packet;
    }

    [Benchmark(Description = "DnsAdBlocker Blocked Domain (Sinkhole)")]
    public byte[]? ProcessBlockedDns() => DnsAdBlocker.ProcessPacket(_blockedPacket, _blockedPacket.Length, true);

    [Benchmark(Description = "DnsAdBlocker Clean Domain (Pass)")]
    public byte[]? ProcessCleanDns() => DnsAdBlocker.ProcessPacket(_allowedPacket, _allowedPacket.Length, true);
}
