namespace obxodka.Models;

public sealed record AuthRequest(
    string Email,
    string Password,
    string Hwid,
    string? DeviceName);
public sealed record GoogleAuthRequest(
    string IdToken,
    string Hwid,
    string? DeviceName);
public sealed record EmailAuthRequest(string Email);
public sealed record EmailVerifyRequest(
    string Email,
    string Code,
    string Hwid,
    string? DeviceName);
public sealed record GoogleRegisterRequest(
    string IdToken,
    string Password,
    string Hwid,
    string? DeviceName);
public sealed record SendCodeRequest(string Email);
public sealed record RegisterRequest(
    string Email,
    string Password,
    string Code,
    string Hwid,
    string? DeviceName);
public sealed record ResetPasswordRequest(
    string Email,
    string Code,
    string NewPassword);
public sealed record LoginResponse(
    string Token,
    string VpnConfig,
    string CertThumbprint,
    DateTime? SubscriptionUntil,
    long BalanceSeconds = 0,
    string Email = "");
public sealed record DeviceItem(
    string? Hwid,
    string? Name,
    DateTime LastActive)
{
    public string LastActiveText => $"Активен: {LastActive.ToLocalTime():dd.MM.yyyy HH:mm}";
    public string DeviceIcon
    {
        get
        {
            var lower = Name?.ToLowerInvariant() ?? "";
            return lower.Contains("windows") || lower.Contains("desktop") || lower.Contains("laptop") || lower.Contains("pc")
                ? "💻"
                : lower.Contains("mac") || lower.Contains("imac") || lower.Contains("macbook")
                ? "🍎"
                : lower.Contains("iphone") || lower.Contains("ipad") || lower.Contains("ios")
                ? "📱"
                : lower.Contains("android") || lower.Contains("samsung") || lower.Contains("pixel")
                ? "🤖"
                : lower.Contains("linux") || lower.Contains("ubuntu") ? "🐧" : "📱";
        }
    }
}
public sealed record VpnStatusResponse(
    bool IsActive,
    long RemainingSeconds);
public record TrafficReportDto(string Thumbprint, long Bytes);
public sealed record SyncRequestDto(
    List<TrafficReportDto> Active,
    List<string> Disconnected);
public sealed record ChangePasswordRequest(string Username, string OldPassword, string NewPassword);
public sealed record UserProfileResponse(DateTime? SubscriptionUntil, long TotalBytesUsed, long BalanceSeconds = 0);
public sealed record VpnServerDto(
    string Ip,
    int Port,
    string Location,
    bool IsOnline,
    int LoadPercent);
public sealed record TelemetryDto(
    string Hwid,
    string AppVersion,
    string Message,
    string StackTrace);
public sealed record MessageResponse([property: System.Text.Json.Serialization.JsonPropertyName("message")] string Message);
public sealed record PaymentLinkResponse([property: System.Text.Json.Serialization.JsonPropertyName("url")] string Url);
