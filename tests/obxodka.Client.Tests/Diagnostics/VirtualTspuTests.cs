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
    public void VirtualTspuDetectsObfuscatorStaticLengthHeader()
    {
        var tspu = new VirtualTspuEngine();
        var packet = new byte[8 + 256];
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), packet.Length);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), 256);
        Random.Shared.NextBytes(packet.AsSpan(8));

        var audit = tspu.InspectPacket(0, packet, isUdp: false);

        Assert.True(audit.DetectedThreats.HasFlag(TspuThreat.ObfuscatorStaticLengthHeader));
    }

    [Fact]
    public void ObxodkaFramingEvadesVirtualTspuDpi()
    {
        var tspu = new VirtualTspuEngine();
        var rawData = new byte[256];
        Random.Shared.NextBytes(rawData);
        var frame = ObxodkaFraming.Pack(ObxodkaFraming.CmdData, rawData, out var frameLen);
        var audit = tspu.InspectPacket(0, frame.AsSpan(0, frameLen), isUdp: false);
        Assert.False(audit.DetectedThreats.HasFlag(TspuThreat.ObfuscatorStaticLengthHeader));
        ArrayPool<byte>.Shared.Return(frame);
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
