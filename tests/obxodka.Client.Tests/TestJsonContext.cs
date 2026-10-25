namespace obxodka.Client.Tests;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(LoginResponse))]
[JsonSerializable(typeof(List<DeviceItem>))]
[JsonSerializable(typeof(DeviceItem))]
[JsonSerializable(typeof(AuthRequest))]
[JsonSerializable(typeof(GoogleAuthRequest))]
[JsonSerializable(typeof(EmailAuthRequest))]
[JsonSerializable(typeof(EmailVerifyRequest))]
[JsonSerializable(typeof(PaymentLinkResponse))]
[JsonSerializable(typeof(MessageResponse))]
[JsonSerializable(typeof(VpnServerDto))]
[JsonSerializable(typeof(TelemetryDto))]
public sealed partial class TestJsonContext : JsonSerializerContext
{
}

