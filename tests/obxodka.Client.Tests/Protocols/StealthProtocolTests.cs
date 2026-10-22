namespace obxodka.Client.Tests.Protocols;

[Trait("Category", "Protocol")]
[Trait("Category", "Stealth")]
[Trait("Category", "Unit")]
public sealed class StealthProtocolTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void PacketRouterTcpPacketsNeverHedgedOnSecondaryRay(int activeRays)
    {
        var pureTcpAck = new byte[40];
        pureTcpAck[0] = 0x45;
        pureTcpAck[9] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(pureTcpAck.AsSpan(2, 2), 40);
        pureTcpAck[12] = 100;
        pureTcpAck[13] = 64;
        pureTcpAck[14] = 0;
        pureTcpAck[15] = 2;
        pureTcpAck[16] = 1;
        pureTcpAck[17] = 1;
        pureTcpAck[18] = 1;
        pureTcpAck[19] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(pureTcpAck.AsSpan(20, 2), 52341);
        BinaryPrimitives.WriteUInt16BigEndian(pureTcpAck.AsSpan(22, 2), 443);
        pureTcpAck[20 + 12] = 0x50;
        pureTcpAck[20 + 13] = 0x10;

        PacketRouter.GetRays(pureTcpAck, pureTcpAck.Length, activeRays, out var primaryRay, out var secondaryRay);

        Assert.InRange(primaryRay, 0, activeRays - 1);
        Assert.Equal(-1, secondaryRay);

        var tcpData = new byte[1420];
        Array.Copy(pureTcpAck, tcpData, 40);
        BinaryPrimitives.WriteUInt16BigEndian(tcpData.AsSpan(2, 2), 1420);

        PacketRouter.GetRays(tcpData, tcpData.Length, activeRays, out var primData, out var secData);
        Assert.InRange(primData, 0, activeRays - 1);
        Assert.Equal(-1, secData);
    }

    [Fact]
    public void PacketRouterSmallUdpAndIcmpHedgedOnSecondaryRayWhenMultipleRays()
    {
        var smallUdp = new byte[128];
        smallUdp[0] = 0x45;
        smallUdp[9] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(smallUdp.AsSpan(2, 2), 128);
        smallUdp[12] = 100;
        smallUdp[13] = 64;
        smallUdp[14] = 0;
        smallUdp[15] = 2;
        smallUdp[16] = 8;
        smallUdp[17] = 8;
        smallUdp[18] = 8;
        smallUdp[19] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(smallUdp.AsSpan(20, 2), 53443);
        BinaryPrimitives.WriteUInt16BigEndian(smallUdp.AsSpan(22, 2), 53);

        PacketRouter.GetRays(smallUdp, smallUdp.Length, 2, out var primaryRay, out var secondaryRay);

        Assert.InRange(primaryRay, 0, 1);
        Assert.InRange(secondaryRay, 0, 1);
        Assert.NotEqual(primaryRay, secondaryRay);

        var icmpPkt = new byte[64];
        icmpPkt[0] = 0x45;
        icmpPkt[9] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(icmpPkt.AsSpan(2, 2), 64);
        icmpPkt[12] = 100;
        icmpPkt[13] = 64;
        icmpPkt[14] = 0;
        icmpPkt[15] = 2;
        icmpPkt[16] = 8;
        icmpPkt[17] = 8;
        icmpPkt[18] = 8;
        icmpPkt[19] = 8;

        PacketRouter.GetRays(icmpPkt, icmpPkt.Length, 2, out var icmpPrim, out var icmpSec);
        Assert.InRange(icmpPrim, 0, 1);
        Assert.InRange(icmpSec, 0, 1);
        Assert.NotEqual(icmpPrim, icmpSec);
    }

    [Fact]
    public void PacketRouterLargeUdpNeverHedgedOnSecondaryRay()
    {
        var largeUdp = new byte[1200];
        largeUdp[0] = 0x45;
        largeUdp[9] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(largeUdp.AsSpan(2, 2), 1200);

        PacketRouter.GetRays(largeUdp, largeUdp.Length, 2, out var primaryRay, out var secondaryRay);
        Assert.InRange(primaryRay, 0, 1);
        Assert.Equal(-1, secondaryRay);
    }

    [Fact]
    public void PacketDeduplicatorDetectsDuplicateForPureTcpAck()
    {
        var deduplicator = new PacketDeduplicator();

        var tcpAck = new byte[40];
        tcpAck[0] = 0x45;
        tcpAck[8] = 64;
        tcpAck[9] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(tcpAck.AsSpan(2, 2), 40);
        tcpAck[12] = 100;
        tcpAck[13] = 64;
        tcpAck[14] = 0;
        tcpAck[15] = 2;
        tcpAck[16] = 1;
        tcpAck[17] = 1;
        tcpAck[18] = 1;
        tcpAck[19] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(tcpAck.AsSpan(20, 2), 52341);
        BinaryPrimitives.WriteUInt16BigEndian(tcpAck.AsSpan(22, 2), 443);
        BinaryPrimitives.WriteUInt32BigEndian(tcpAck.AsSpan(24, 4), 1000500);
        BinaryPrimitives.WriteUInt32BigEndian(tcpAck.AsSpan(28, 4), 2000600);
        tcpAck[20 + 12] = 0x50;
        tcpAck[20 + 13] = 0x10;

        var first = deduplicator.IsDuplicate(tcpAck, tcpAck.Length);
        var second = deduplicator.IsDuplicate(tcpAck, tcpAck.Length);

        Assert.False(first);
        Assert.True(second);
    }

    [Fact]
    public void PacketDeduplicatorDistinctTcpAcksNotMarkedAsDuplicate()
    {
        var deduplicator = new PacketDeduplicator();

        var tcpAck1 = new byte[40];
        tcpAck1[0] = 0x45;
        tcpAck1[8] = 64;
        tcpAck1[9] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(tcpAck1.AsSpan(2, 2), 40);
        tcpAck1[12] = 100;
        tcpAck1[13] = 64;
        tcpAck1[14] = 0;
        tcpAck1[15] = 2;
        tcpAck1[16] = 1;
        tcpAck1[17] = 1;
        tcpAck1[18] = 1;
        tcpAck1[19] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(tcpAck1.AsSpan(20, 2), 52341);
        BinaryPrimitives.WriteUInt16BigEndian(tcpAck1.AsSpan(22, 2), 443);
        BinaryPrimitives.WriteUInt32BigEndian(tcpAck1.AsSpan(24, 4), 1000500);
        BinaryPrimitives.WriteUInt32BigEndian(tcpAck1.AsSpan(28, 4), 2000600);
        tcpAck1[20 + 12] = 0x50;
        tcpAck1[20 + 13] = 0x10;

        var tcpAck2 = (byte[])tcpAck1.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(tcpAck2.AsSpan(28, 4), 2002060);

        var first = deduplicator.IsDuplicate(tcpAck1, tcpAck1.Length);
        var second = deduplicator.IsDuplicate(tcpAck2, tcpAck2.Length);

        Assert.False(first);
        Assert.False(second);
    }

    [Fact]
    public void PacketDeduplicatorIgnoresTtlHopDelta()
    {
        var deduplicator = new PacketDeduplicator();

        var ray0Packet = new byte[64];
        ray0Packet[0] = 0x45;
        ray0Packet[8] = 64;
        ray0Packet[9] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(ray0Packet.AsSpan(2, 2), 64);
        ray0Packet[12] = 10;
        ray0Packet[13] = 0;
        ray0Packet[14] = 0;
        ray0Packet[15] = 1;
        ray0Packet[16] = 10;
        ray0Packet[17] = 0;
        ray0Packet[18] = 0;
        ray0Packet[19] = 2;
        Random.Shared.NextBytes(ray0Packet.AsSpan(20));

        var ray1Packet = (byte[])ray0Packet.Clone();
        ray1Packet[8] = 62;

        var first = deduplicator.IsDuplicate(ray0Packet, ray0Packet.Length);
        var second = deduplicator.IsDuplicate(ray1Packet, ray1Packet.Length);

        Assert.False(first);
        Assert.True(second);
    }

    [Fact]
    public void PriorityPacketQueuePrioritizesPureTcpAckAndPingOverBulk()
    {
        using var queue = new PriorityPacketQueue(100);

        var bulkTcp = new byte[1400];
        bulkTcp[0] = 0x45;
        bulkTcp[9] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(bulkTcp.AsSpan(2, 2), 1400);
        bulkTcp[20 + 12] = 0x50;
        bulkTcp[20 + 13] = 0x18;
        var obfuscatedBulk = Obfuscator.Pack(bulkTcp, bulkTcp.Length, out var bulkLen);

        var pureAck = new byte[40];
        pureAck[0] = 0x45;
        pureAck[9] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(pureAck.AsSpan(2, 2), 40);
        pureAck[20 + 12] = 0x50;
        pureAck[20 + 13] = 0x10;
        var obfuscatedAck = Obfuscator.Pack(pureAck, pureAck.Length, out var ackLen);

        Span<byte> pingProbe = stackalloc byte[9];
        pingProbe[0] = 0x99;
        BinaryPrimitives.WriteInt64LittleEndian(pingProbe.Slice(1, 8), Stopwatch.GetTimestamp());
        var obfuscatedPing = Obfuscator.Pack(pingProbe, out var pingLen);

        Assert.True(queue.TryEnqueue(obfuscatedBulk, bulkLen));
        Assert.True(queue.TryEnqueue(obfuscatedAck, ackLen));
        Assert.True(queue.TryEnqueue(obfuscatedPing, pingLen));

        Assert.True(queue.TryDequeue(out var firstOut));
        Assert.True(Obfuscator.TryDecodeLengths(firstOut.buffer.AsSpan(0, 8), out _, out var firstLen));
        Assert.True(firstLen <= 40);

        Assert.True(queue.TryDequeue(out var secondOut));
        Assert.True(Obfuscator.TryDecodeLengths(secondOut.buffer.AsSpan(0, 8), out _, out var secondLen));
        Assert.True(secondLen <= 40);

        Assert.True(queue.TryDequeue(out var thirdOut));
        Assert.True(Obfuscator.TryDecodeLengths(thirdOut.buffer.AsSpan(0, 8), out _, out var thirdLen));
        Assert.Equal(1400, thirdLen);

        ArrayPool<byte>.Shared.Return(firstOut.buffer);
        ArrayPool<byte>.Shared.Return(secondOut.buffer);
        ArrayPool<byte>.Shared.Return(thirdOut.buffer);
    }

    [Fact]
    public void PriorityPacketQueueFairSchedulingDeqeuesFromLowPriorityPeriodically()
    {
        using var queue = new PriorityPacketQueue(200);

        for (var i = 0; i < 30; i++)
        {
            var bulk = new byte[1200];
            bulk[0] = 0x45;
            bulk[9] = 6;
            BinaryPrimitives.WriteUInt16BigEndian(bulk.AsSpan(2, 2), 1200);
            var packed = Obfuscator.Pack(bulk, bulk.Length, out var len);
            _ = queue.TryEnqueue(packed, len);
        }

        for (var i = 0; i < 30; i++)
        {
            var ping = new byte[9];
            ping[0] = 0x99;
            var packed = Obfuscator.Pack(ping, ping.Length, out var len);
            _ = queue.TryEnqueue(packed, len);
        }

        var lowPriorityFound = false;
        for (var i = 0; i < 16; i++)
        {
            if (queue.TryDequeue(out var item))
            {
                _ = Obfuscator.TryDecodeLengths(item.buffer.AsSpan(0, 8), out _, out var realLen);
                if (realLen == 1200)
                {
                    lowPriorityFound = true;
                }
                ArrayPool<byte>.Shared.Return(item.buffer);
            }
        }

        Assert.True(lowPriorityFound);
    }

    [Fact]
    public void PriorityPacketQueueDrainAndReturnEmptiesAllQueuesAndResetsSemaphore()
    {
        using var queue = new PriorityPacketQueue(100);

        for (var i = 0; i < 10; i++)
        {
            var buf = ArrayPool<byte>.Shared.Rent(64);
            buf[8] = 0x99;
            _ = queue.TryEnqueue(buf, 64);
        }

        Assert.True(queue.Count > 0);
        queue.DrainAndReturn();
        Assert.Equal(0, queue.Count);
        Assert.False(queue.TryDequeue(out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(1420)]
    public void ObxodkaFramingSimdMaskAndUnmaskReversibleAcrossVariousBufferSizes(int length)
    {
        var original = new byte[length];
        Random.Shared.NextBytes(original);

        var copy = (byte[])original.Clone();
        const byte salt = 0x5C;

        ObxodkaFraming.MaskPayload(copy, salt);
        Assert.NotEqual(original, copy);

        ObxodkaFraming.UnmaskPayload(copy, salt);
        Assert.Equal(original, copy);
    }

    [Fact]
    public void ObxodkaFramingWriteFrameAndTryDecodeHeaderRoundTrip()
    {
        var payload = "Test Obxodka Framing Payload Zero-Copy"u8.ToArray();
        const byte cmd = ObxodkaFraming.CmdData;
        const int paddingLen = 16;
        var totalLen = ObxodkaFraming.HeaderSize + payload.Length + paddingLen;

        var buffer = new byte[totalLen];
        ObxodkaFraming.WriteFrame(buffer.AsSpan(), cmd, payload, paddingLen);

        var ok = ObxodkaFraming.TryDecodeHeader(buffer.AsSpan(0, ObxodkaFraming.HeaderSize), out var decTotal, out var decPayload, out var decCmd, out var salt);

        Assert.True(ok);
        Assert.Equal(totalLen, decTotal);
        Assert.Equal(payload.Length, decPayload);
        Assert.Equal(cmd, decCmd);

        var extracted = buffer.AsSpan(ObxodkaFraming.HeaderSize, decPayload).ToArray();
        ObxodkaFraming.UnmaskPayload(extracted, salt);
        Assert.Equal(payload, extracted);
    }

    [Fact]
    public void ClientHelloBuilderGeneratesStrictRfcCompliantTlsRecordWithoutDuplicateExtensionLength()
    {
        var sni = "vpn.obxodka.one"u8;
        var hello = ClientHelloBuilder.BuildChrome120ClientHello(sni);

        Assert.NotNull(hello);
        Assert.True(hello.Length >= 100);

        Assert.Equal(0x16, hello[0]);
        Assert.Equal(0x03, hello[1]);
        Assert.Equal(0x01, hello[2]);

        var recordLength = BinaryPrimitives.ReadUInt16BigEndian(hello.AsSpan(3, 2));
        Assert.Equal(hello.Length - 5, recordLength);

        Assert.Equal(0x01, hello[5]);

        var handshakeLength = (hello[6] << 16) | (hello[7] << 8) | hello[8];
        Assert.Equal(recordLength - 4, handshakeLength);

        Assert.Equal(0x03, hello[9]);
        Assert.Equal(0x03, hello[10]);

        var offset = 11 + 32;

        var sessionIdLen = hello[offset++];
        offset += sessionIdLen;

        var cipherSuitesLen = BinaryPrimitives.ReadUInt16BigEndian(hello.AsSpan(offset, 2));
        offset += 2 + cipherSuitesLen;

        var compressionMethodsLen = hello[offset++];
        offset += compressionMethodsLen;

        var extensionsLen = BinaryPrimitives.ReadUInt16BigEndian(hello.AsSpan(offset, 2));
        offset += 2;

        var extensionsStart = offset;
        var extensionsEnd = extensionsStart + extensionsLen;
        Assert.Equal(hello.Length, extensionsEnd);

        var extensionCount = 0;
        while (offset < extensionsEnd)
        {
            _ = BinaryPrimitives.ReadUInt16BigEndian(hello.AsSpan(offset, 2));
            var extLen = BinaryPrimitives.ReadUInt16BigEndian(hello.AsSpan(offset + 2, 2));
            offset += 4 + extLen;
            extensionCount++;
        }

        Assert.Equal(extensionsEnd, offset);
        Assert.True(extensionCount >= 5);
    }

    [Fact]
    public void ObxodkaFramingCalculatePaddingLengthZeroOverheadForBulkPackets()
    {
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(0, ObxodkaFraming.CalculatePaddingLength(700));
            Assert.Equal(0, ObxodkaFraming.CalculatePaddingLength(1100));
            Assert.Equal(0, ObxodkaFraming.CalculatePaddingLength(1420));
            Assert.InRange(ObxodkaFraming.CalculatePaddingLength(64), 8, 64);
            Assert.InRange(ObxodkaFraming.CalculatePaddingLength(400), 8, 40);
        }
    }
    [Fact]
    public void RayInspectorDetectsAsymmetricBlackholeAndMigratesRay()
    {
        var inspector = new obxodka.Core.Transports.RayInspector();
        var now = Stopwatch.GetTimestamp();

        inspector.RecordTx(0, now - (long)(Stopwatch.Frequency * 0.2));
        inspector.RecordRx(0, now - (long)(Stopwatch.Frequency * 2.5));

        inspector.RecordTx(1, now - (long)(Stopwatch.Frequency * 0.1));
        inspector.RecordRx(1, now - (long)(Stopwatch.Frequency * 0.1));

        var detected = inspector.DetectAsymmetricBlackhole(0, now, out var fallbackRay);
        Assert.True(detected);
        Assert.Equal(1, fallbackRay);
    }

    [Fact]
    public void RayInspectorFailoverMaintainsHedgingOnRay2()
    {
        var inspector = new obxodka.Core.Transports.RayInspector();
        var now = Stopwatch.GetTimestamp();

        inspector.RecordRx(0, now - (long)(Stopwatch.Frequency * 5.0));
        inspector.RecordRx(1, now - (long)(Stopwatch.Frequency * 0.1));
        inspector.RecordRx(2, now - (long)(Stopwatch.Frequency * 0.1));

        inspector.SelectRaysForPacket(
            preferredRay: 1,
            needsHedging: true,
            activeRays: 3,
            nowTs: now,
            out var primaryRay,
            out var duplicateRay);

        Assert.Equal(1, primaryRay);
        Assert.Equal(2, duplicateRay);
    }

    [Fact]
    public void PacketRouterThreeRaysRoutesGamingUdpToRay2()
    {
        var robloxUdp = new byte[800];
        robloxUdp[0] = 0x45;
        robloxUdp[9] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(robloxUdp.AsSpan(2, 2), 800);
        BinaryPrimitives.WriteUInt16BigEndian(robloxUdp.AsSpan(20, 2), 54321);
        BinaryPrimitives.WriteUInt16BigEndian(robloxUdp.AsSpan(22, 2), 59123);

        PacketRouter.GetRays(robloxUdp, robloxUdp.Length, 3, out var primaryRay, out var secondaryRay);
        Assert.Equal(0, primaryRay);
        Assert.Equal(2, secondaryRay);
    }

    [Fact]
    public void RayInspectorAdaptiveMtuDownshiftAndStepLogging()
    {
        var inspector = new obxodka.Core.Transports.RayInspector();
        Assert.True(inspector.TryDownshiftMtu(1400, out var mtu1));
        Assert.Equal(1360, mtu1);

        Assert.True(inspector.TryDownshiftMtu(mtu1, out var mtu2));
        Assert.Equal(1300, mtu2);

        Assert.True(inspector.TryDownshiftMtu(mtu2, out var mtu3));
        Assert.Equal(1280, mtu3);

        Assert.False(inspector.TryDownshiftMtu(mtu3, out var mtu4));
        Assert.Equal(1280, mtu4);

        var steps = inspector.GetDiagnosticSteps();
        Assert.Contains("1400 -> 1360", steps);
        Assert.Contains("1360 -> 1300", steps);
        Assert.Contains("1300 -> 1280", steps);
    }

    [Fact]
    public void RayInspectorDetectsMtuBlackhole()
    {
        var inspector = new obxodka.Core.Transports.RayInspector();
        var now = Stopwatch.GetTimestamp();

        inspector.RecordRx(0, now);
        for (var i = 0; i < 25; i++)
        {
            inspector.RecordDataTx();
        }

        Assert.False(inspector.DetectMtuBlackhole(now));
        Assert.False(inspector.DetectMtuBlackhole(now));
        Assert.True(inspector.DetectMtuBlackhole(now));

        inspector.RecordDataRx();
        Assert.False(inspector.DetectMtuBlackhole(now));
    }

    [Fact]
    public async Task RayInspectorRateLimitsIncidentReportsAsync()
    {
        var inspector = new obxodka.Core.Transports.RayInspector();
        inspector.RecordStep("Initial step");

        _ = await inspector.ReportIncidentIfExhaustedAsync("TestReason", "example.com", 443, 1280, apiBaseUrl: "http://127.0.0.1:59999");
        var secondAttempt = await inspector.ReportIncidentIfExhaustedAsync("TestReason", "example.com", 443, 1280, apiBaseUrl: "http://127.0.0.1:59999");

        Assert.False(secondAttempt);
    }
}
