namespace obxodka.Client.Tests.Diagnostics;

[Trait("Category", "Unit")]
public class VirtualTspuTests
{
    [Fact]
    public void VirtualTspuDetectsWireGuardHandshake()
    {
        var tspu = new VirtualTspuEngine();
        var wgPacket = new byte[148];
        wgPacket[0] = 0x01;
        Random.Shared.NextBytes(wgPacket.AsSpan(1));

        var audit = tspu.InspectPacket(0, wgPacket, isUdp: true);

        Assert.True(audit.DetectedThreats.HasFlag(TspuThreat.WireGuardHandshake));
    }

    [Fact]
    public void VirtualTspuDetectsFechsueQuicHeaderAndHighEntropy()
    {
        var tspu = new VirtualTspuEngine();
        var authPacket = FechsueCodec.PackAuth("aabbccddeeff00112233445566778899aabbccdd", 0, out var authLen);

        var auditAuth = tspu.InspectPacket(0, authPacket.AsSpan(0, authLen), isUdp: true);
        Assert.True(auditAuth.DetectedThreats.HasFlag(TspuThreat.QuicInitialThrottled));

        var key = new byte[32];
        Random.Shared.NextBytes(key);
        using var aes = new AesGcm(key, 16);

        var packets = new List<ReadOnlyMemory<byte>> { authPacket.AsMemory(0, authLen) };
        for (var i = 0; i < 5; i++)
        {
            var dummyPayload = new byte[512];
            Random.Shared.NextBytes(dummyPayload);
            var encrypted = FechsueCodec.Pack(dummyPayload, dummyPayload.Length, 0x12345678, aes, out var encLen, maskSessionId: false);
            packets.Add(encrypted.AsMemory(0, encLen));
        }

        var report = tspu.AnalyzeStream(packets, isUdp: true);

        Assert.True(report.TotalFlags.HasFlag(TspuThreat.QuicInitialThrottled));
        Assert.True(report.TotalFlags.HasFlag(TspuThreat.HighEntropyCryptoAnomaly));
        Assert.True(report.TotalFlags.HasFlag(TspuThreat.FechsueStaticSessionLeak));
        Assert.False(report.IsStealthPassed);
    }

    [Fact]
    public void VirtualTspuDetectsObfuscatorStaticLengthHeader()
    {
        var tspu = new VirtualTspuEngine();
        var rawPacket = new byte[256];
        Random.Shared.NextBytes(rawPacket);

        var packed = Obfuscator.Pack(rawPacket, rawPacket.Length, out var totalLen, maskHeaders: false);
        var audit = tspu.InspectPacket(0, packed.AsSpan(0, totalLen), isUdp: false);

        Assert.True(audit.DetectedThreats.HasFlag(TspuThreat.ObfuscatorStaticLengthHeader));
    }

    [Fact]
    public void HardenedProtocolsEvadeVirtualTspuDpi()
    {
        var tspu = new VirtualTspuEngine();
        var stealthAuth = FechsueCodec.PackStealthAuth("aabbccddeeff00112233445566778899aabbccdd", 0, out var authLen);

        var auditAuth = tspu.InspectPacket(0, stealthAuth.AsSpan(0, authLen), isUdp: true);
        Assert.False(auditAuth.DetectedThreats.HasFlag(TspuThreat.QuicInitialThrottled));

        var key = new byte[32];
        Random.Shared.NextBytes(key);
        using var aes = new AesGcm(key, 16);

        var packets = new List<ReadOnlyMemory<byte>>();
        for (var i = 0; i < 5; i++)
        {
            var dummyPayload = new byte[512];
            Random.Shared.NextBytes(dummyPayload);
            var encrypted = FechsueCodec.Pack(dummyPayload, dummyPayload.Length, 0x12345678, aes, out var encLen, maskSessionId: true);
            packets.Add(encrypted.AsMemory(0, encLen));
        }

        var report = tspu.AnalyzeStream(packets, isUdp: true);
        Assert.False(report.TotalFlags.HasFlag(TspuThreat.FechsueStaticSessionLeak));

        var rawData = new byte[256];
        Random.Shared.NextBytes(rawData);
        var maskedObfs = Obfuscator.Pack(rawData, rawData.Length, out var obfsLen, maskHeaders: true);
        var obfsAudit = tspu.InspectPacket(0, maskedObfs.AsSpan(0, obfsLen), isUdp: false);
        Assert.False(obfsAudit.DetectedThreats.HasFlag(TspuThreat.ObfuscatorStaticLengthHeader));
    }

    [Fact]
    public void DpiBypassStreamSplitsClientHelloAndEvadesFullSniReconstruction()
    {
        var tlsHello = new byte[]
        {
            0x16, 0x03, 0x01, 0x00, 0x43,
            0x01, 0x00, 0x00, 0x3F,
            0x03, 0x03,
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
            0x00,
            0x00, 0x02, 0x13, 0x01,
            0x01, 0x00,
            0x00, 0x14,
            0x00, 0x00, 0x00, 0x10,
            0x00, 0x0E, 0x00, 0x00, 0x0B, 0x64, 0x69, 0x73, 0x63, 0x6F, 0x72, 0x64, 0x2E, 0x63, 0x6F, 0x6D
        };

        var tspu = new VirtualTspuEngine();
        var fullAudit = tspu.InspectPacket(0, tlsHello, isUdp: false);
        Assert.True(fullAudit.DetectedThreats.HasFlag(TspuThreat.CleartextSniDetected));

        using var mem = new MemoryStream();
        using var bypass = new DpiBypassStream(mem, splitPosition: 2, delayMs: 0);
        bypass.Write(tlsHello);

        var firstSegment = tlsHello.AsSpan(0, 2);
        var splitAudit = tspu.InspectPacket(0, firstSegment, isUdp: false);

        Assert.False(splitAudit.DetectedThreats.HasFlag(TspuThreat.CleartextSniDetected));
    }

    [Fact]
    public void VirtualTspuPassesStealthWhenTrafficHasNaturalEntropyAndNoSignatures()
    {
        var tspu = new VirtualTspuEngine();
        var naturalPackets = new List<ReadOnlyMemory<byte>>();

        for (var i = 0; i < 10; i++)
        {
            var packet = new byte[128];
            var offset = i * 7 % 16;
            for (var j = 0; j < packet.Length; j++)
            {
                packet[j] = (byte)((j % 32) + offset);
            }
            naturalPackets.Add(packet);
        }

        var report = tspu.AnalyzeStream(naturalPackets, isUdp: true);

        Assert.True(report.IsStealthPassed);
        Assert.Equal(0, report.BlockedCount);
        Assert.True(report.AverageEntropy < 7.45);
    }
}
