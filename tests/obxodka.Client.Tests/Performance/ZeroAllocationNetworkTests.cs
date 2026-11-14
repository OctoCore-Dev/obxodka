namespace obxodka.Client.Tests.Performance;

[CollectionDefinition("Non-Parallel", DisableParallelization = true)]
public class NonParallelCollectionDefinition;

[Collection("Non-Parallel")]
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

        for (var w = 0; w < 50; w++)
        {
            PacketRouter.GetRays(packet.AsSpan(), 3, out _, out _);
        }

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5000; i++)
        {
            PacketRouter.GetRays(packet.AsSpan(), 3, out var r1, out var r2);
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

        for (var w = 0; w < 50; w++)
        {
            PacketRouter.GetRays(packet, 3, out _, out _);
        }

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5000; i++)
        {
            PacketRouter.GetRays(packet, 3, out var r1, out var r2);
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

        for (var w = 0; w < 500; w++)
        {
            _ = deduplicator.IsDuplicate(packet.AsSpan());
        }

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5000; i++)
        {
            _ = deduplicator.IsDuplicate(packet.AsSpan());
        }
        var allocAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, allocAfter - allocBefore);
    }

    [Fact]
    public void ObxodkaFramingTryDecodeHeaderHasZeroAllocations()
    {
        Span<byte> frame = stackalloc byte[32];
        Span<byte> payload = stackalloc byte[16];
        payload.Fill(0x42);
        ObxodkaFraming.WriteFrame(frame, ObxodkaFraming.CmdData, payload, paddingLen: 0, sessionKey: 0);

        for (var w = 0; w < 50; w++)
        {
            _ = ObxodkaFraming.TryDecodeHeader(frame, out _, out _, out _, out _);
        }

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5000; i++)
        {
            _ = ObxodkaFraming.TryDecodeHeader(frame, out var tLen, out var pLen, out var cmd, out var salt);
            _ = tLen + pLen + cmd + salt;
        }
        var allocAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, allocAfter - allocBefore);
    }

    [Fact]
    public void ObxodkaFramingPackHasZeroGarbageAllocations()
    {
        Span<byte> probe = stackalloc byte[9];
        probe[0] = 0x99;
        BinaryPrimitives.WriteInt64LittleEndian(probe.Slice(1, 8), Stopwatch.GetTimestamp());

        for (var w = 0; w < 50; w++)
        {
            var warmup = ObxodkaFraming.Pack(ObxodkaFraming.CmdData, probe, out _);
            ArrayPool<byte>.Shared.Return(warmup);
        }

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            var packed = ObxodkaFraming.Pack(ObxodkaFraming.CmdData, probe, out var totalLength);
            _ = totalLength;
            ArrayPool<byte>.Shared.Return(packed);
        }
        var allocAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, allocAfter - allocBefore);
    }

    [Fact]
    public void PriorityPacketQueueCorrectlyPrioritizesPingProbes()
    {
        using var queue = new PriorityPacketQueue(100);

        var pingProbe = new byte[9];
        pingProbe[0] = 0x99;
        BinaryPrimitives.WriteInt64LittleEndian(pingProbe.AsSpan(1, 8), Stopwatch.GetTimestamp());

        var bulkTcp = new byte[1400];
        bulkTcp[0] = 0x45;
        bulkTcp[9] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(bulkTcp.AsSpan(2, 2), 1400);
        bulkTcp[20 + 12] = 0x50;

        Assert.True(queue.TryEnqueue(bulkTcp, bulkTcp.Length));
        Assert.True(queue.TryEnqueue(pingProbe, pingProbe.Length));

        Assert.True(queue.TryDequeue(out var firstOut));
        Assert.Equal(9, firstOut.length);
        Assert.Equal(0x99, firstOut.buffer[0]);

        Assert.True(queue.TryDequeue(out var secondOut));
        Assert.Equal(1400, secondOut.length);
    }

    [Fact]
    public void PriorityPacketQueueCorrectlyPrioritizesIcmp()
    {
        using var queue = new PriorityPacketQueue(100);

        var icmpPkt = new byte[28];
        icmpPkt[0] = 0x45;
        icmpPkt[9] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(icmpPkt.AsSpan(2, 2), 28);

        var bulkTcp = new byte[1200];
        bulkTcp[0] = 0x45;
        bulkTcp[9] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(bulkTcp.AsSpan(2, 2), 1200);
        bulkTcp[20 + 12] = 0x50;

        Assert.True(queue.TryEnqueue(bulkTcp, bulkTcp.Length));
        Assert.True(queue.TryEnqueue(icmpPkt, icmpPkt.Length));

        Assert.True(queue.TryDequeue(out var firstOut));
        Assert.Equal(28, firstOut.length);

        if (queue.TryDequeue(out var secondOut))
        {
            Assert.Equal(1200, secondOut.length);
        }
    }

}
