namespace obxodka.Client.Tests.Performance;

[Trait("Category", "Performance")]
[Trait("Category", "Unit")]
public class ZeroAllocationNetworkTests : IDisposable
{
    private readonly byte[] _key = SHA256.HashData("TestKey_ZeroAllocation_2026"u8.ToArray());
    private readonly AesGcm _aes;

    public ZeroAllocationNetworkTests() => _aes = new AesGcm(_key, 16);

    public void Dispose()
    {
        _aes.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void PacketRouterGetRaysHasZeroAllocations()
    {
        var packet = new byte[1420];
        packet[0] = 0x45;
        packet[9] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), 1420);
        packet[12] = 192;
        packet[13] = 168;
        packet[14] = 1;
        packet[15] = 10;
        packet[16] = 104;
        packet[17] = 21;
        packet[18] = 45;
        packet[19] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20, 2), 54321);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), 443);

        PacketRouter.GetRays(packet, packet.Length, 8, out _, out _);

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5000; i++)
        {
            PacketRouter.GetRays(packet, packet.Length, 8, out var r1, out var r2);
            _ = r1 + r2;
        }
        var allocAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, allocAfter - allocBefore);
    }

    [Fact]
    public void PacketRouterGetRaysSpanHasZeroAllocations()
    {
        ReadOnlySpan<byte> packet =
        [
            0x45, 0x00, 0x00, 0x3c, 0x1c, 0x46, 0x40, 0x00, 0x40, 0x06, 0xb1, 0xe6,
            0xc0, 0xa8, 0x01, 0x0a, 0x68, 0x15, 0x2d, 0x02, 0xd4, 0x31, 0x01, 0xbb,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x50, 0x02, 0x72, 0x10,
            0x00, 0x00, 0x00, 0x00
        ];

        PacketRouter.GetRays(packet, 8, out _, out _);

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5000; i++)
        {
            PacketRouter.GetRays(packet, 8, out var r1, out var r2);
            _ = r1 + r2;
        }
        var allocAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, allocAfter - allocBefore);
    }

    [Fact]
    public void PacketDeduplicatorHasZeroAllocations()
    {
        var deduplicator = new PacketDeduplicator();
        var packet = new byte[64];
        packet[0] = 0x45;
        packet[9] = 17;
        Random.Shared.NextBytes(packet.AsSpan(10));

        _ = deduplicator.IsDuplicate(packet, packet.Length);

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5000; i++)
        {
            _ = deduplicator.IsDuplicate(packet, packet.Length);
        }
        var allocAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, allocAfter - allocBefore);
    }

    [Fact]
    public void ObfuscatorTryDecodeLengthsHasZeroAllocations()
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header[..4], 1428 ^ Obfuscator.ObfsHeaderMask);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4, 4), 1420 ^ Obfuscator.ObfsPayloadMask);

        _ = Obfuscator.TryDecodeLengths(header, out _, out _);

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5000; i++)
        {
            _ = Obfuscator.TryDecodeLengths(header, out var totalLen, out var realLen);
            _ = totalLen + realLen;
        }
        var allocAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, allocAfter - allocBefore);
    }

    [Fact]
    public void ObfuscatorPackSpanHasZeroGarbageAllocations()
    {
        Span<byte> probe = stackalloc byte[9];
        probe[0] = 0x99;
        BinaryPrimitives.WriteInt64LittleEndian(probe.Slice(1, 8), Stopwatch.GetTimestamp());

        var warmup = Obfuscator.Pack(probe, out _);
        ArrayPool<byte>.Shared.Return(warmup);

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            var packed = Obfuscator.Pack(probe, out var totalLength);
            _ = totalLength;
            ArrayPool<byte>.Shared.Return(packed);
        }
        var allocAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, allocAfter - allocBefore);
    }

    [Fact]
    public void FechsueCodecPackSpanHasZeroGarbageAllocations()
    {
        Span<byte> probe = stackalloc byte[10];
        probe[0] = 0x99;
        probe[1] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(probe.Slice(2, 8), Stopwatch.GetTimestamp());

        var warmup = FechsueCodec.Pack(probe, 12345, _aes, out _);
        ArrayPool<byte>.Shared.Return(warmup);

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            var packed = FechsueCodec.Pack(probe, 12345, _aes, out var totalLength);
            _ = totalLength;
            ArrayPool<byte>.Shared.Return(packed);
        }
        var allocAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, allocAfter - allocBefore);
    }

    [Fact]
    public void PriorityPacketQueueCorrectlyPrioritizesObfuscatedPingProbes()
    {
        using var queue = new PriorityPacketQueue(100);

        Span<byte> pingProbe = stackalloc byte[9];
        pingProbe[0] = 0x99;
        BinaryPrimitives.WriteInt64LittleEndian(pingProbe.Slice(1, 8), Stopwatch.GetTimestamp());
        var obfuscatedPing = Obfuscator.Pack(pingProbe, out var pingTotalLen);

        var bulkTcp = new byte[1400];
        bulkTcp[0] = 0x45;
        bulkTcp[9] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(bulkTcp.AsSpan(2, 2), 1400);
        bulkTcp[20 + 12] = 0x50;
        var obfuscatedBulk = Obfuscator.Pack(bulkTcp, bulkTcp.Length, out var bulkTotalLen);

        Assert.True(queue.TryEnqueue(obfuscatedBulk, bulkTotalLen));
        Assert.True(queue.TryEnqueue(obfuscatedPing, pingTotalLen));

        Assert.True(queue.TryDequeue(out var firstOut));
        Assert.True(Obfuscator.TryDecodeLengths(firstOut.buffer.AsSpan(0, 8), out _, out var firstRealLen));
        Assert.Equal(9, firstRealLen);
        Assert.Equal(0x99, firstOut.buffer[8]);

        Assert.True(queue.TryDequeue(out var secondOut));
        Assert.True(Obfuscator.TryDecodeLengths(secondOut.buffer.AsSpan(0, 8), out _, out var secondRealLen));
        Assert.Equal(1400, secondRealLen);

        ArrayPool<byte>.Shared.Return(firstOut.buffer);
        ArrayPool<byte>.Shared.Return(secondOut.buffer);
    }

    [Fact]
    public void PriorityPacketQueueCorrectlyPrioritizesObfuscatedIcmp()
    {
        using var queue = new PriorityPacketQueue(100);

        var icmpPkt = new byte[28];
        icmpPkt[0] = 0x45;
        icmpPkt[9] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(icmpPkt.AsSpan(2, 2), 28);
        var obfuscatedIcmp = Obfuscator.Pack(icmpPkt, icmpPkt.Length, out var icmpTotalLen);

        var bulkTcp = new byte[1200];
        bulkTcp[0] = 0x45;
        bulkTcp[9] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(bulkTcp.AsSpan(2, 2), 1200);
        bulkTcp[20 + 12] = 0x50;
        var obfuscatedBulk = Obfuscator.Pack(bulkTcp, bulkTcp.Length, out var bulkTotalLen);

        Assert.True(queue.TryEnqueue(obfuscatedBulk, bulkTotalLen));
        Assert.True(queue.TryEnqueue(obfuscatedIcmp, icmpTotalLen));

        Assert.True(queue.TryDequeue(out var firstOut));
        Assert.True(Obfuscator.TryDecodeLengths(firstOut.buffer.AsSpan(0, 8), out _, out var firstRealLen));
        Assert.Equal(28, firstRealLen);

        ArrayPool<byte>.Shared.Return(firstOut.buffer);
        if (queue.TryDequeue(out var secondOut))
        {
            ArrayPool<byte>.Shared.Return(secondOut.buffer);
        }
    }
}
