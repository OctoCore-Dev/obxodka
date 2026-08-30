namespace obxodka.Client.Tests.Protocols;

[Trait("Category", "Protocol")]
[Trait("Category", "Unit")]
public class DisconnectProtocolTests
{
    [Fact]
    public void PackAuthWithDiscMagicCreatesValidDisconnectPacket()
    {
        var thumbprint = "TEST-THUMBPRINT-DISCONNECT-123456";
        var packet = FechsueCodec.PackDisc(thumbprint, out var len);

        Assert.True(len >= 25);

        var unpacked = FechsueCodec.TryUnpackDisc(packet.AsSpan(0, len), out var unpackedThumb, out _);
        Assert.True(unpacked);
        Assert.Equal(thumbprint, unpackedThumb);

        ArrayPool<byte>.Shared.Return(packet);
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(45, "00:45")]
    [InlineData(125, "02:05")]
    [InlineData(3665, "01:01:05")]
    [InlineData(7325, "02:02:05")]
    public void TimeFormatHelperFormatsSecondsCorrectly(long seconds, string expectedShort)
    {
        var formatted = TimeFormatHelper.FormatSeconds(seconds, false);
        Assert.Equal(expectedShort, formatted);
    }

    [Theory]
    [InlineData(0, "0ч 00м 00с")]
    [InlineData(45, "00м 45с")]
    [InlineData(125, "02м 05с")]
    [InlineData(3665, "1ч 01м 05с")]
    public void TimeFormatHelperFormatsVerboseSecondsCorrectly(long seconds, string expectedVerbose)
    {
        var formatted = TimeFormatHelper.FormatSeconds(seconds, true);
        Assert.Equal(expectedVerbose, formatted);
    }
}
