namespace obxodka.Client.Tests;

[Trait("Category", "Unit")]
public class ModelsSerializationTests
{
    [Fact]
    public void AuthRequestRoundTripSerialization()
    {
        var original = new AuthRequest("user@example.com", "secretPass", "hwid-12345", "Xiaomi 13");
        var json = JsonSerializer.Serialize(original, TestJsonContext.Default.AuthRequest);
        var deserialized = JsonSerializer.Deserialize(json, TestJsonContext.Default.AuthRequest);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Email, deserialized.Email);
        Assert.Equal(original.Password, deserialized.Password);
        Assert.Equal(original.Hwid, deserialized.Hwid);
        Assert.Equal(original.DeviceName, deserialized.DeviceName);
    }

    [Fact]
    public void GoogleAuthRequestRoundTrip()
    {
        var original = new GoogleAuthRequest("google_id_token_xyz", "hwid-9999", "Pixel 8");
        var json = JsonSerializer.Serialize(original, TestJsonContext.Default.GoogleAuthRequest);
        var deserialized = JsonSerializer.Deserialize(json, TestJsonContext.Default.GoogleAuthRequest);

        Assert.NotNull(deserialized);
        Assert.Equal(original.IdToken, deserialized.IdToken);
        Assert.Equal(original.Hwid, deserialized.Hwid);
        Assert.Equal(original.DeviceName, deserialized.DeviceName);
    }

    [Fact]
    public void PaymentLinkResponseJsonPropertyNameUrlMapping()
    {
        var rawJson = "{\"url\":\"https://payment.provider.com/pay/12345\"}";
        var deserialized = JsonSerializer.Deserialize(rawJson, TestJsonContext.Default.PaymentLinkResponse);

        Assert.NotNull(deserialized);
        Assert.Equal("https://payment.provider.com/pay/12345", deserialized.Url);
    }

    [Fact]
    public void MessageResponseJsonPropertyNameMessageMapping()
    {
        var rawJson = "{\"message\":\"Успешная операция\"}";
        var deserialized = JsonSerializer.Deserialize(rawJson, TestJsonContext.Default.MessageResponse);

        Assert.NotNull(deserialized);
        Assert.Equal("Успешная операция", deserialized.Message);
    }

    [Fact]
    public void VpnServerDtoRoundTripSerialization()
    {
        var original = new VpnServerDto("1.2.3.4", 443, "DE - Frankfurt", true, 22);
        var json = JsonSerializer.Serialize(original, TestJsonContext.Default.VpnServerDto);
        var deserialized = JsonSerializer.Deserialize(json, TestJsonContext.Default.VpnServerDto);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Ip, deserialized.Ip);
        Assert.Equal(original.Port, deserialized.Port);
        Assert.Equal(original.Location, deserialized.Location);
        Assert.Equal(original.IsOnline, deserialized.IsOnline);
        Assert.Equal(original.LoadPercent, deserialized.LoadPercent);
    }

    [Fact]
    public void DeviceItemIconCalculations()
    {
        var windowsDevice = new DeviceItem("hwid_1", "My Windows 11 Desktop", DateTime.UtcNow);
        var appleDevice = new DeviceItem("hwid_2", "MacBook Pro M3", DateTime.UtcNow);
        var androidDevice = new DeviceItem("hwid_3", "Samsung Galaxy S24", DateTime.UtcNow);

        Assert.Equal("Desktop", windowsDevice.DeviceIcon);
        Assert.Equal("Mac", appleDevice.DeviceIcon);
        Assert.Equal("Android", androidDevice.DeviceIcon);
    }

    [Fact]
    public void TelemetryDtoSerialization()
    {
        var telemetry = new TelemetryDto("hwid_xyz", "3.7.18", "Socket connection dropped", "at FechsueTransport.Receive()");
        var json = JsonSerializer.Serialize(telemetry, TestJsonContext.Default.TelemetryDto);

        Assert.Contains("hwid_xyz", json);
        Assert.Contains("3.7.18", json);
        Assert.Contains("Socket connection dropped", json);
    }
}
