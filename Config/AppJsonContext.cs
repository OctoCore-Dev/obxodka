using System.Text.Json.Serialization;

namespace obxodka.Config;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AuthRequest))]
[JsonSerializable(typeof(LoginResponse))]
[JsonSerializable(typeof(ChangePasswordRequest))]
[JsonSerializable(typeof(List<DeviceItem>))]
[JsonSerializable(typeof(DeviceItem))]
[JsonSerializable(typeof(UserSession))]
[JsonSerializable(typeof(AppInfoItem))]
[JsonSerializable(typeof(List<AppInfoItem>))]
[JsonSerializable(typeof(UserProfileResponse))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(VpnServerDto))]
[JsonSerializable(typeof(List<VpnServerDto>))]
[JsonSerializable(typeof(object), TypeInfoPropertyName = "JsonObject")]
internal sealed partial class AppJsonContext : JsonSerializerContext
{
}
