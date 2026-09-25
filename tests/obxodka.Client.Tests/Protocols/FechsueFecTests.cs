namespace obxodka.Client.Tests.Protocols;

[Trait("Category", "Protocols")]
[Trait("Category", "Unit")]
public class FechsueFecTests
{
    private static readonly byte[] t_key = SHA256.HashData("test_fec_key"u8.ToArray());
    private const uint SessionId = 0x12345678;

    [Fact]
    public void FecDecoderInstantlyRecoversSingleDroppedPacketInGroup()
    {
        using var crypto = new AesGcm(t_key, 16);
        var encoder = new FechsueCodec.FecEncoder(groupSize: 4);
        var decoder = new FechsueCodec.FecDecoder();

        var packets = new byte[][]
        {
            Encoding.UTF8.GetBytes("Packet 0: Gaming UDP voice frame"),
            Encoding.UTF8.GetBytes("Packet 1: Video stream segment H264"),
            Encoding.UTF8.GetBytes("Packet 2: CRITICAL_PACKET_DROPPED_BY_DPI_TSPU"),
            Encoding.UTF8.GetBytes("Packet 3: ACK response data block")
        };

        var encodedFrames = new List<byte[]>();
        byte[]? parityFrame = null;
        var parityLen = 0;

        for (var i = 0; i < packets.Length; i++)
        {
            var (dataPacked, dataLen, pPacked, pLen) = encoder.Encode(packets[i], packets[i].Length, SessionId, crypto);
            var copy = new byte[dataLen];
            Buffer.BlockCopy(dataPacked, 0, copy, 0, dataLen);
            ArrayPool<byte>.Shared.Return(dataPacked);
            encodedFrames.Add(copy);

            if (pPacked != null)
            {
                parityFrame = new byte[pLen];
                Buffer.BlockCopy(pPacked, 0, parityFrame, 0, pLen);
                parityLen = pLen;
                ArrayPool<byte>.Shared.Return(pPacked);
            }
        }

        Assert.NotNull(parityFrame);
        Assert.True(parityLen > 0);

        var receivedPackets = new List<string>();

        Assert.True(FechsueCodec.TryUnpack(encodedFrames[0], encodedFrames[0].Length, crypto, out _, out var p0, out var l0));
        Assert.True(decoder.ProcessPayload(p0!, l0, out var d0, out var dl0, out var r0, out _));
        Assert.NotNull(d0);
        receivedPackets.Add(Encoding.UTF8.GetString(d0, 0, dl0));
        ArrayPool<byte>.Shared.Return(p0!);
        ArrayPool<byte>.Shared.Return(d0!);

        Assert.True(FechsueCodec.TryUnpack(encodedFrames[1], encodedFrames[1].Length, crypto, out _, out var p1, out var l1));
        Assert.True(decoder.ProcessPayload(p1!, l1, out var d1, out var dl1, out var r1, out _));
        Assert.NotNull(d1);
        receivedPackets.Add(Encoding.UTF8.GetString(d1, 0, dl1));
        ArrayPool<byte>.Shared.Return(p1!);
        ArrayPool<byte>.Shared.Return(d1!);

        Assert.True(FechsueCodec.TryUnpack(encodedFrames[3], encodedFrames[3].Length, crypto, out _, out var p3, out var l3));
        Assert.True(decoder.ProcessPayload(p3!, l3, out var d3, out var dl3, out var r3, out _));
        Assert.NotNull(d3);
        receivedPackets.Add(Encoding.UTF8.GetString(d3, 0, dl3));
        ArrayPool<byte>.Shared.Return(p3!);
        ArrayPool<byte>.Shared.Return(d3!);

        Assert.True(FechsueCodec.TryUnpack(parityFrame, parityLen, crypto, out _, out var pParity, out var lParity));
        Assert.True(decoder.ProcessPayload(pParity!, lParity, out _, out _, out var recoveredPkt, out var recoveredLen));
        ArrayPool<byte>.Shared.Return(pParity!);

        Assert.NotNull(recoveredPkt);
        var recoveredText = Encoding.UTF8.GetString(recoveredPkt, 0, recoveredLen);
        ArrayPool<byte>.Shared.Return(recoveredPkt!);

        Assert.Equal("Packet 2: CRITICAL_PACKET_DROPPED_BY_DPI_TSPU", recoveredText);
    }
}
