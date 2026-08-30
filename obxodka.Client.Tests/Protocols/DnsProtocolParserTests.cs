namespace obxodka.Client.Tests.Protocols;

[Trait("Category", "Protocol")]
[Trait("Category", "Unit")]
public class DnsProtocolParserTests
{
    private static byte[] BuildDnsHeader(ushort id, ushort flags, ushort questions, ushort answers)
    {
        var header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0, 2), id);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), flags);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4, 2), questions);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6, 2), answers);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10, 2), 0);
        return header;
    }

    [Fact]
    public void StandardDnsQueryHeaderDecoding()
    {
        var header = BuildDnsHeader(0xABCD, 0x0100, 1, 0);

        var id = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
        var flags = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
        var qdCount = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
        var anCount = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6, 2));

        Assert.Equal(0xABCD, id);
        Assert.Equal(0x0100, flags);
        Assert.Equal(1, qdCount);
        Assert.Equal(0, anCount);
    }

    [Fact]
    public void MalformedShortDnsPacketReturnsNullGracefully()
    {
        var tooShort = new byte[15];
        var result = DnsAdBlocker.ProcessPacket(tooShort, tooShort.Length, useAdblock: true);
        Assert.Null(result);
    }
}
