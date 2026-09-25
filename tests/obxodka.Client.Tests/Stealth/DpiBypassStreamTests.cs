namespace obxodka.Client.Tests;

[Trait("Category", "Unit")]
public class DpiBypassStreamTests
{
    [Fact]
    public async Task WriteAsyncSplitsTlsClientHelloCorrectlyAsync()
    {
        using var memoryStream = new MemoryStream();
        using var bypassStream = new DpiBypassStream(memoryStream);

        var tlsData = new byte[] { 0x16, 0x03, 0x01, 0x00, 0x50, 0x01, 0x00, 0x00, 0x4C, 0x03, 0x03 };
        await bypassStream.WriteAsync(tlsData.AsMemory());
        await bypassStream.FlushAsync();

        var writtenData = memoryStream.ToArray();
        Assert.Equal(tlsData.Length, writtenData.Length);
        Assert.Equal(tlsData, writtenData);
    }

    [Fact]
    public async Task WriteAsyncSmallPayloadDoesNotThrowAsync()
    {
        using var memoryStream = new MemoryStream();
        using var bypassStream = new DpiBypassStream(memoryStream);

        var smallData = "Hi"u8.ToArray();
        await bypassStream.WriteAsync(smallData.AsMemory());
        await bypassStream.FlushAsync();

        Assert.Equal(smallData, memoryStream.ToArray());
    }

    [Fact]
    public async Task ReadAsyncPassesThroughToUnderlyingStreamAsync()
    {
        var sourceBytes = "Incoming VPN Stream"u8.ToArray();
        using var memoryStream = new MemoryStream(sourceBytes);
        using var bypassStream = new DpiBypassStream(memoryStream);

        var readBuffer = new byte[sourceBytes.Length];
        var bytesRead = await bypassStream.ReadAsync(readBuffer.AsMemory());

        Assert.Equal(sourceBytes.Length, bytesRead);
        Assert.Equal(sourceBytes, readBuffer);
    }
}
