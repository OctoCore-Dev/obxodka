namespace obxodka.Client.Tests;

[Trait("Category", "Unit")]
public class OctopusProtocolTests
{
    [Fact]
    public async Task PumpTrafficAsyncCopiesDataCorrectlyAsync()
    {
        var inputData = "Hello, VPN traffic!";
        var inputBytes = Encoding.UTF8.GetBytes(inputData);
        using var inputStream = new MemoryStream(inputBytes);
        using var outputStream = new MemoryStream();

        using var cts = new CancellationTokenSource();

        _ = await OctopusProtocol.PumpTrafficAsync(inputStream, outputStream, cts.Token);

        var resultData = Encoding.UTF8.GetString(outputStream.ToArray());
        Assert.Equal(inputData, resultData);
    }
}
