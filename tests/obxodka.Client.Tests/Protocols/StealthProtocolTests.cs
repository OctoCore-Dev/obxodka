using obxodka.Shared.Stealth.ClientHello;
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

        PacketRouter.GetRays(pureTcpAck.AsSpan(), activeRays, out var primaryRay, out var secondaryRay);

        Assert.InRange(primaryRay, 0, activeRays - 1);
        Assert.Equal(-1, secondaryRay);

        var tcpData = new byte[1420];
        Array.Copy(pureTcpAck, tcpData, 40);
        BinaryPrimitives.WriteUInt16BigEndian(tcpData.AsSpan(2, 2), 1420);

        PacketRouter.GetRays(tcpData.AsSpan(), activeRays, out var primData, out var secData);
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

        PacketRouter.GetRays(smallUdp.AsSpan(), 2, out var primaryRay, out var secondaryRay);

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

        PacketRouter.GetRays(icmpPkt.AsSpan(), 2, out var icmpPrim, out var icmpSec);
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

        PacketRouter.GetRays(largeUdp.AsSpan(), 2, out var primaryRay, out var secondaryRay);
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

        var first = deduplicator.IsDuplicate(tcpAck.AsSpan());
        var second = deduplicator.IsDuplicate(tcpAck.AsSpan());

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

        var first = deduplicator.IsDuplicate(tcpAck1.AsSpan());
        var second = deduplicator.IsDuplicate(tcpAck2.AsSpan());

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

        var first = deduplicator.IsDuplicate(ray0Packet.AsSpan());
        var second = deduplicator.IsDuplicate(ray1Packet.AsSpan());

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

        var pureAck = new byte[40];
        pureAck[0] = 0x45;
        pureAck[9] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(pureAck.AsSpan(2, 2), 40);
        pureAck[20 + 12] = 0x50;
        pureAck[20 + 13] = 0x10;

        var pingProbe = new byte[9];
        pingProbe[0] = 0x99;
        BinaryPrimitives.WriteInt64LittleEndian(pingProbe.AsSpan(1, 8), Stopwatch.GetTimestamp());

        Assert.True(queue.TryEnqueue(bulkTcp, bulkTcp.Length));
        Assert.True(queue.TryEnqueue(pureAck, pureAck.Length));
        Assert.True(queue.TryEnqueue(pingProbe, pingProbe.Length));

        Assert.True(queue.TryDequeue(out var firstOut));
        Assert.True(firstOut.length <= 40);

        Assert.True(queue.TryDequeue(out var secondOut));
        Assert.True(secondOut.length <= 40);

        Assert.True(queue.TryDequeue(out var thirdOut));
        Assert.Equal(1400, thirdOut.length);
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
            _ = queue.TryEnqueue(bulk, bulk.Length);
        }

        for (var i = 0; i < 30; i++)
        {
            var ping = new byte[9];
            ping[0] = 0x99;
            _ = queue.TryEnqueue(ping, ping.Length);
        }

        var lowPriorityFound = false;
        for (var i = 0; i < 16; i++)
        {
            if (queue.TryDequeue(out var item))
            {
                if (item.length == 1200)
                {
                    lowPriorityFound = true;
                }
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
        var sni = Encoding.UTF8.GetBytes($"vpn.{AppConfig.PrimaryDomain}");
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
    public void FirefoxHelloGeneratesValidTlsRecordWithoutGrease()
    {
        var sni = "mozilla.org"u8;
        var hello = FirefoxHello.Build(sni);

        Assert.NotNull(hello);
        Assert.True(hello.Length >= 100);
        Assert.Equal(0x16, hello[0]);
        Assert.Equal(0x03, hello[1]);
        Assert.Equal(0x01, hello[2]);

        var recordLength = BinaryPrimitives.ReadUInt16BigEndian(hello.AsSpan(3, 2));
        Assert.Equal(hello.Length - 5, recordLength);
        Assert.Equal(0x01, hello[5]);
    }

    [Fact]
    public void SafariHelloGeneratesValidTlsRecord()
    {
        var sni = "apple.com"u8;
        var hello = SafariHello.Build(sni, isMobile: true);

        Assert.NotNull(hello);
        Assert.True(hello.Length >= 100);
        Assert.Equal(0x16, hello[0]);
        Assert.Equal(0x03, hello[1]);
        Assert.Equal(0x01, hello[2]);

        var recordLength = BinaryPrimitives.ReadUInt16BigEndian(hello.AsSpan(3, 2));
        Assert.Equal(hello.Length - 5, recordLength);
        Assert.Equal(0x01, hello[5]);
    }

    [Fact]
    public void ChromePostQuantumGeneratesLargerClientHello()
    {
        var sni = "google.com"u8;
        var classicalHello = ChromiumHello.Build(sni, isPostQuantum: false);
        var pqHello = ChromiumHello.Build(sni, isPostQuantum: true);

        Assert.True(pqHello.Length > classicalHello.Length);
        Assert.True(pqHello.Length >= 1300);
    }

    [Fact]
    public void RoutingHelloCyclesThroughAllProfilesCorrectly()
    {
        var sni = "obxodka.one"u8;
        var profiles = new List<TlsBrowserProfile>();

        for (var i = 0; i < 6; i++)
        {
            var hello = RoutingHello.GetNextHello(sni, i, out var usedProfile);
            Assert.NotNull(hello);
            Assert.Equal(0x16, hello[0]);
            profiles.Add(usedProfile);
        }

        Assert.Contains(TlsBrowserProfile.Chrome, profiles);
        Assert.Contains(TlsBrowserProfile.Yandex, profiles);
        Assert.Contains(TlsBrowserProfile.Firefox, profiles);
        Assert.Contains(TlsBrowserProfile.SafariIos, profiles);
    }

    [Fact]
    public void ObxodkaFramingCalculatePaddingLengthZeroOverheadForBulkPackets()
    {
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(0, ObxodkaFraming.CalculatePaddingLength(ObxodkaFraming.CmdData, 700));
            Assert.Equal(0, ObxodkaFraming.CalculatePaddingLength(ObxodkaFraming.CmdData, 1100));
            Assert.Equal(0, ObxodkaFraming.CalculatePaddingLength(ObxodkaFraming.CmdData, 1420));
            Assert.InRange(ObxodkaFraming.CalculatePaddingLength(ObxodkaFraming.CmdData, 64), 8, 64);
            Assert.InRange(ObxodkaFraming.CalculatePaddingLength(ObxodkaFraming.CmdData, 400), 8, 40);
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
    public void RayInspectorNeverRoutesUserDataToInspectorRay2()
    {
        var inspector = new obxodka.Core.Transports.RayInspector();
        var now = Stopwatch.GetTimestamp();

        // Ray 0 мертв (молчал 5 секунд), Ray 1 жив, Ray 2 (инспектор) жив
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
        // ВАЖНО: Ray 2 - это Control Plane! Он не должен принимать пользовательские данные,
        // поэтому дублировать некуда (второй рабочий Data-луч Ray 0 мертв -> duplicateRay == -1):
        Assert.Equal(-1, duplicateRay);
    }

    [Fact]
    public void PacketRouterThreeRaysRoutesGamingUdpBetweenDataRays()
    {
        var robloxUdp = new byte[800];
        robloxUdp[0] = 0x45;
        robloxUdp[9] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(robloxUdp.AsSpan(2, 2), 800);
        BinaryPrimitives.WriteUInt16BigEndian(robloxUdp.AsSpan(20, 2), 54321);
        BinaryPrimitives.WriteUInt16BigEndian(robloxUdp.AsSpan(22, 2), 59123);

        PacketRouter.GetRays(robloxUdp.AsSpan(), 3, out var primaryRay, out var secondaryRay);
        Assert.Equal(0, primaryRay);
        // Хеджирование идет строго во второй рабочий Data-луч (Ray 1), но НИКОГДА в инспектор Ray 2:
        Assert.Equal(1, secondaryRay);
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

        Assert.True(inspector.TryDownshiftMtu(mtu3, out var mtu4));
        Assert.Equal(1240, mtu4);

        Assert.True(inspector.TryDownshiftMtu(mtu4, out var mtu5));
        Assert.Equal(1200, mtu5);

        Assert.False(inspector.TryDownshiftMtu(mtu5, out var mtu6));
        Assert.Equal(1200, mtu6);

        var steps = inspector.GetDiagnosticSteps();
        Assert.Contains("1400 -> 1360", steps);
        Assert.Contains("1360 -> 1300", steps);
        Assert.Contains("1300 -> 1280", steps);
        Assert.Contains("1280 -> 1240", steps);
        Assert.Contains("1240 -> 1200", steps);
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

    [Fact]
    public void PacketDeduplicatorDetectsDuplicateForGamingUdpLargePacket()
    {
        var deduplicator = new PacketDeduplicator();

        var robloxUdp = new byte[800];
        robloxUdp[0] = 0x45;
        robloxUdp[8] = 64;
        robloxUdp[9] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(robloxUdp.AsSpan(2, 2), 800);
        robloxUdp[12] = 100;
        robloxUdp[13] = 64;
        robloxUdp[14] = 0;
        robloxUdp[15] = 2;
        robloxUdp[16] = 128;
        robloxUdp[17] = 116;
        robloxUdp[18] = 40;
        robloxUdp[19] = 50;
        BinaryPrimitives.WriteUInt16BigEndian(robloxUdp.AsSpan(20, 2), 54321);
        BinaryPrimitives.WriteUInt16BigEndian(robloxUdp.AsSpan(22, 2), 59123);
        Random.Shared.NextBytes(robloxUdp.AsSpan(28, 772));

        var ray0Dup = (byte[])robloxUdp.Clone();
        var ray2Dup = (byte[])robloxUdp.Clone();
        ray2Dup[8] = 58;

        var first = deduplicator.IsDuplicate(ray0Dup.AsSpan());
        var second = deduplicator.IsDuplicate(ray2Dup.AsSpan());

        Assert.False(first);
        Assert.True(second);
    }

    [Fact]
    public void PacketDeduplicatorDistinctLargePacketsWithIdentical256BytePrefixNotMarkedAsDuplicate()
    {
        var deduplicator = new PacketDeduplicator();

        var packet1 = new byte[1200];
        packet1[0] = 0x45;
        packet1[8] = 64;
        packet1[9] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(packet1.AsSpan(2, 2), 1200);
        packet1[12] = 100;
        packet1[13] = 64;
        packet1[14] = 0;
        packet1[15] = 2;
        packet1[16] = 128;
        packet1[17] = 116;
        packet1[18] = 40;
        packet1[19] = 50;
        BinaryPrimitives.WriteUInt16BigEndian(packet1.AsSpan(20, 2), 54321);
        BinaryPrimitives.WriteUInt16BigEndian(packet1.AsSpan(22, 2), 59123);
        for (var i = 28; i < 256; i++)
        {
            packet1[i] = (byte)(i & 0xFF);
        }
        for (var i = 256; i < 1200; i++)
        {
            packet1[i] = 0xAA;
        }

        var packet2 = (byte[])packet1.Clone();
        for (var i = 256; i < 1200; i++)
        {
            packet2[i] = 0xBB;
        }

        var first = deduplicator.IsDuplicate(packet1.AsSpan());
        var second = deduplicator.IsDuplicate(packet2.AsSpan());

        Assert.False(first);
        Assert.False(second);
    }

    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36 Edg/133.0.0.0", null, TlsBrowserProfile.Edge)]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 YaBrowser/25.2.0.0 Safari/537.36", null, TlsBrowserProfile.Yandex)]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 OPR/116.0.0.0", null, TlsBrowserProfile.Opera)]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:135.0) Gecko/20100101 Firefox/135.0", null, TlsBrowserProfile.Firefox)]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_3 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.3 Mobile/15E148 Safari/604.1", null, TlsBrowserProfile.SafariIos)]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_7_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.3 Safari/605.1.15", null, TlsBrowserProfile.SafariMac)]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36", null, TlsBrowserProfile.Chrome)]
    [InlineData("CustomAgent/1.0", "\"Microsoft Edge\";v=\"133\"", TlsBrowserProfile.Edge)]
    [InlineData("CustomAgent/1.0", "\"Opera\";v=\"116\"", TlsBrowserProfile.Opera)]
    public void DetectProfileIdentifiesBrowserCorrectly(string? userAgent, string? secChUa, TlsBrowserProfile expectedProfile)
    {
        var detected = RoutingHello.DetectProfile(userAgent, secChUa);
        Assert.Equal(expectedProfile, detected);
    }

    [Theory]
    [InlineData(TlsBrowserProfile.Edge, "Microsoft-IIS/10.0", "X-MSEdge-Ref")]
    [InlineData(TlsBrowserProfile.Yandex, "nginx", "X-Yandex-Req-Id")]
    [InlineData(TlsBrowserProfile.Opera, "cloudflare", "Alt-Svc")]
    [InlineData(TlsBrowserProfile.Firefox, "nginx", null)]
    [InlineData(TlsBrowserProfile.SafariIos, "Apple", "x-apple-orig-url")]
    [InlineData(TlsBrowserProfile.SafariMac, "Apple", "x-apple-orig-url")]
    [InlineData(TlsBrowserProfile.Chrome, "gws", "Alt-Svc")]
    public void GetServerResponseHeadersMatchesDetectedBrowser(TlsBrowserProfile profile, string expectedServer, string? expectedCustomHeader)
    {
        var headers = RoutingHello.GetServerResponseHeaders(profile);
        Assert.Contains(headers, h => h.Key == "Server" && h.Value == expectedServer);

        if (expectedCustomHeader is not null)
        {
            Assert.Contains(headers, h => h.Key.Equals(expectedCustomHeader, StringComparison.OrdinalIgnoreCase));
        }

        // Никаких Accept-CH для Firefox и Safari!
        if (profile is TlsBrowserProfile.Firefox or TlsBrowserProfile.SafariIos or TlsBrowserProfile.SafariMac)
        {
            Assert.DoesNotContain(headers, h => h.Key.Equals("Accept-CH", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    [InlineData(TlsBrowserProfile.Edge, "EdgeHTML")]
    [InlineData(TlsBrowserProfile.Yandex, "YaBrowser")]
    [InlineData(TlsBrowserProfile.Opera, "118.0.0.0")]
    [InlineData(TlsBrowserProfile.Firefox, "Gecko")]
    [InlineData(TlsBrowserProfile.SafariIos, "WebKit")]
    [InlineData(TlsBrowserProfile.SafariMac, "WebKit")]
    [InlineData(TlsBrowserProfile.Chrome, "133.0")]
    public void GetDecoyJsonResponseMatchesDetectedBrowser(TlsBrowserProfile profile, string expectedSubstring)
    {
        var json = RoutingHello.GetDecoyJsonResponse(profile);
        Assert.Contains(expectedSubstring, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyBrowserHeadersSetsExpectedHeadersAndClientHints()
    {
        using var reqChrome = new HttpRequestMessage();
        RoutingHello.ApplyBrowserHeaders(reqChrome, TlsBrowserProfile.Chrome);
        Assert.True(reqChrome.Headers.Contains("User-Agent"));
        Assert.True(reqChrome.Headers.Contains("sec-ch-ua"));

        using var reqFirefox = new HttpRequestMessage();
        RoutingHello.ApplyBrowserHeaders(reqFirefox, TlsBrowserProfile.Firefox);
        Assert.True(reqFirefox.Headers.Contains("User-Agent"));
        Assert.False(reqFirefox.Headers.Contains("sec-ch-ua")); // Firefox не отправляет sec-ch-ua

        using var reqSafari = new HttpRequestMessage();
        RoutingHello.ApplyBrowserHeaders(reqSafari, TlsBrowserProfile.SafariMac);
        Assert.True(reqSafari.Headers.Contains("User-Agent"));
        Assert.False(reqSafari.Headers.Contains("sec-ch-ua")); // Safari не отправляет sec-ch-ua
    }

    [Fact]
    public void GetProfileForAttemptRotatesProfilesSequentially()
    {
        var p0 = RoutingHello.GetProfileForAttempt(0);
        var p1 = RoutingHello.GetProfileForAttempt(1);
        var p2 = RoutingHello.GetProfileForAttempt(2);
        var p3 = RoutingHello.GetProfileForAttempt(3);
        var p4 = RoutingHello.GetProfileForAttempt(4);
        var p5 = RoutingHello.GetProfileForAttempt(5);

        Assert.Equal(TlsBrowserProfile.Chrome, p0);
        Assert.Equal(TlsBrowserProfile.Yandex, p1);
        Assert.Equal(TlsBrowserProfile.Firefox, p2);
        Assert.Equal(TlsBrowserProfile.SafariIos, p3);
        Assert.Equal(TlsBrowserProfile.Edge, p4);
        Assert.Equal(TlsBrowserProfile.ChromePostQuantum, p5);
    }

    [Fact]
    public void TryFindSniMidsldOffsetLocatesMiddleOfSecondLevelDomain()
    {
        var sni = Encoding.UTF8.GetBytes("vpn.obxodka.one");
        var hello = ClientHelloBuilder.BuildChrome120ClientHello(sni);

        var found = DpiBypassStream.TryFindSniMidsldOffset(hello, out var splitOffset);
        Assert.True(found);
        Assert.True(splitOffset > 43);
        Assert.True(splitOffset < hello.Length);

        //проверяем что срез попадает внутрь доменного имени obxodka
        var helloStr = Encoding.Latin1.GetString(hello);
        var sniIndex = helloStr.IndexOf("vpn.obxodka.one", StringComparison.Ordinal);
        Assert.True(sniIndex >= 0);
        var obxodkaStart = sniIndex + 4;
        var obxodkaEnd = obxodkaStart + 7;
        Assert.True(splitOffset >= obxodkaStart && splitOffset <= obxodkaEnd);
    }

    [Fact]
    public async Task DpiBypassStreamSplitsClientHelloAtMidsldInPureL4ModeAsync()
    {
        var sni = Encoding.UTF8.GetBytes("obxodka.one");
        var hello = ClientHelloBuilder.BuildChrome120ClientHello(sni);

        var underlying = new TestRecordingStream();
        await using var dpiStream = new DpiBypassStream(underlying, delayMs: 1)
        {
            Mode = DpiBypassMode.PureL4Midsld
        };

        await dpiStream.WriteAsync(hello);

        Assert.Equal(2, underlying.Writes.Count);
        var slice1 = underlying.Writes[0];
        var slice2 = underlying.Writes[1];
        Assert.Equal(hello.Length, slice1.Length + slice2.Length);

        var reassembled = new byte[hello.Length];
        Buffer.BlockCopy(slice1, 0, reassembled, 0, slice1.Length);
        Buffer.BlockCopy(slice2, 0, reassembled, slice1.Length, slice2.Length);
        Assert.Equal(hello, reassembled);

        var dummy = new byte[] { 1, 2, 3, 4, 5 };
        await dpiStream.WriteAsync(dummy);
        Assert.Equal(3, underlying.Writes.Count);
        Assert.Equal(dummy, underlying.Writes[2]);
    }

    [Fact]
    public async Task DpiBypassStreamSplitsClientHelloIntoTwoValidRecordsInTlsRecordModeAsync()
    {
        var sni = Encoding.UTF8.GetBytes("obxodka.one");
        var hello = ClientHelloBuilder.BuildChrome120ClientHello(sni);

        var underlying = new TestRecordingStream();
        await using var dpiStream = new DpiBypassStream(underlying, delayMs: 1)
        {
            Mode = DpiBypassMode.TlsRecordMidsld
        };

        await dpiStream.WriteAsync(hello);

        Assert.Equal(2, underlying.Writes.Count);
        var rec1 = underlying.Writes[0];
        var rec2 = underlying.Writes[1];

        Assert.Equal(0x16, rec1[0]);
        Assert.Equal(0x03, rec1[1]);
        Assert.Equal(0x16, rec2[0]);
        Assert.Equal(0x03, rec2[1]);

        var payload1 = rec1.AsSpan(5);
        var payload2 = rec2.AsSpan(5);
        Assert.Equal(hello.Length - 5, payload1.Length + payload2.Length);

        var reassembledPayload = new byte[payload1.Length + payload2.Length];
        payload1.CopyTo(reassembledPayload.AsSpan(0, payload1.Length));
        payload2.CopyTo(reassembledPayload.AsSpan(payload1.Length));
        Assert.Equal(hello.AsSpan(5).ToArray(), reassembledPayload);
    }

    [Fact]
    public void SniEngineFiltersInternalBrandHostAndBuildsCandidates()
    {
        Assert.True(SniEngine.IsInternalBrandHost("vpn.obxodka.one"));
        Assert.True(SniEngine.IsInternalBrandHost("node.octocore.net"));
        Assert.False(SniEngine.IsInternalBrandHost("login.live.com"));

        var candidates = SniEngine.BuildStreamCandidates("70.34.201.253", 443, "vpn.obxodka.one");
        Assert.NotEmpty(candidates);
        Assert.All(candidates, c => Assert.False(SniEngine.IsInternalBrandHost(c.TargetHost)));
    }

    [Fact]
    public void SniEngineStreamRouteAndSliceWindowReturnRealisticValues()
    {
        var route0 = SniEngine.GetStreamRoute(0, 1, out var mime0);
        var route1 = SniEngine.GetStreamRoute(0, 2, out var mime1);
        Assert.NotEqual(route0, route1);
        Assert.False(string.IsNullOrWhiteSpace(mime0));
        Assert.False(string.IsNullOrWhiteSpace(mime1));
        Assert.NotEqual("application/octet-stream", mime0);
        Assert.NotEqual("application/octet-stream", RoutingHello.GetStreamResponseContentType(route0));

        SniEngine.CalculateStreamSliceWindow(0, out var maxTicks0, out var maxBytes0);
        SniEngine.CalculateStreamSliceWindow(1, out var maxTicks1, out var maxBytes1);
        Assert.True(maxTicks0 > 0);
        Assert.True(maxTicks1 > maxTicks0 / 2);
        Assert.InRange(maxBytes0, 24L * 1024 * 1024, 64L * 1024 * 1024);
        Assert.InRange(maxBytes1, 32L * 1024 * 1024, 64L * 1024 * 1024);
    }

    private sealed class TestRecordingStream : Stream
    {
        public List<byte[]> Writes { get; } = [];

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get; set; }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override long Seek(long offset, SeekOrigin origin) => 0;

        public override void SetLength(long value) { }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var copy = new byte[count];
            Buffer.BlockCopy(buffer, offset, copy, 0, count);
            Writes.Add(copy);
        }

        public override void Write(ReadOnlySpan<byte> buffer) => Writes.Add(buffer.ToArray());

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes.Add(buffer.ToArray());
            return ValueTask.CompletedTask;
        }
    }
}
