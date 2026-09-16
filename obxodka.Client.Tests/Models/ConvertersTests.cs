namespace obxodka.Client.Tests;

[Trait("Category", "Unit")]
public class DeviceClassifierTests
{
    [Theory]
    [InlineData("Windows 11 PC", DeviceKind.Desktop)]
    [InlineData("My Gaming Desktop", DeviceKind.Desktop)]
    [InlineData("MacBook Pro M2", DeviceKind.Desktop)]
    [InlineData("Samsung Galaxy S24", DeviceKind.Phone)]
    [InlineData("iPhone 15 Pro", DeviceKind.Phone)]
    [InlineData("Google Pixel 8", DeviceKind.Phone)]
    [InlineData("Custom Device", DeviceKind.Unknown)]
    [InlineData("", DeviceKind.Unknown)]
    [InlineData(null, DeviceKind.Unknown)]
    public void DeviceClassifierReturnsCorrectType(string? deviceName, DeviceKind expectedType)
    {
        var result = DeviceClassifier.Classify(deviceName);
        Assert.Equal(expectedType, result);
    }
}
