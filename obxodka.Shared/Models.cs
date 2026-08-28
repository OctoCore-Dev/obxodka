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

    public string DeviceIcon => (Name?.ToLowerInvariant() ?? string.Empty) switch
    {
        var s when s.Contains("windows", StringComparison.OrdinalIgnoreCase) ||
                   s.Contains("desktop", StringComparison.OrdinalIgnoreCase) ||
                   s.Contains("laptop", StringComparison.OrdinalIgnoreCase) ||
                   s.Contains("pc", StringComparison.OrdinalIgnoreCase) => "Desktop",
        var s when s.Contains("mac", StringComparison.OrdinalIgnoreCase) ||
                   s.Contains("imac", StringComparison.OrdinalIgnoreCase) ||
                   s.Contains("macbook", StringComparison.OrdinalIgnoreCase) => "Mac",
        var s when s.Contains("iphone", StringComparison.OrdinalIgnoreCase) ||
                   s.Contains("ipad", StringComparison.OrdinalIgnoreCase) ||
                   s.Contains("ios", StringComparison.OrdinalIgnoreCase) => "iOS",
        var s when s.Contains("android", StringComparison.OrdinalIgnoreCase) ||
                   s.Contains("samsung", StringComparison.OrdinalIgnoreCase) ||
                   s.Contains("pixel", StringComparison.OrdinalIgnoreCase) => "Android",
        var s when s.Contains("linux", StringComparison.OrdinalIgnoreCase) ||
                   s.Contains("ubuntu", StringComparison.OrdinalIgnoreCase) => "Linux",
        _ => "Phone"
    };
}

public sealed record VpnStatusResponse(
    bool IsActive,
    long RemainingSeconds);

public sealed record TrafficReportDto(string Thumbprint, long Bytes);

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

public sealed record MessageResponse([property: JsonPropertyName("message")] string Message);

public sealed record PaymentLinkResponse([property: JsonPropertyName("url")] string Url);

public sealed record PaymentUrlResponse(string Url);

public sealed record GooglePurchaseVerifyRequest(string ProductId, string PurchaseToken, string? OrderId);

public sealed record GooglePurchaseVerifyResponse(bool Success, int SecondsAdded = 0, long NewBalance = 0, string? Message = null);

public sealed record PostReviewRequest(string Text, int? Rating, Guid? ParentId);

public sealed record PostReviewResponse(string Message, Guid Id);

public sealed record ReviewReplyDto(Guid Id, string? Author, string Text, DateTime CreatedAt);

public sealed record ReviewDto(Guid Id, string? Author, string Text, int? Rating, int Likes, DateTime CreatedAt, List<ReviewReplyDto> Replies);

public sealed record LikeResponse(int Likes);

public sealed class HydraConfig
{
    public string ActiveBridge { get; set; } = "https://obxodka.one";
    public DateTime UpdatedAt { get; set; }
}

public sealed record ReferralFriendDto(string EmailMasked, DateTime ActivatedAt, int BonusHours);
public sealed record ReferralCodeResponse(string Code, int ActivatedCount, long BalanceSeconds, long TotalMeshBytesRelayed, List<ReferralFriendDto> Friends);
public sealed record ActivateReferralRequest(string Code);
public sealed record ClaimRewardRequest(string ClaimId);
public sealed record ClaimRewardResponse(int HoursGranted, long NewTotalSeconds);

public sealed record RegisterRelayRequest(int Port, string? CountryCode, string? CountryFlag);
public sealed record RegisterRelayResponse(string RelayId, string Status);
public sealed record RelayHeartbeatRequest(string RelayId);
public sealed record ValidateRelayJwtRequest(string ClientToken);
public sealed record ValidateRelayJwtResponse(bool Valid, bool IsFriend, string UserIdHash);
public sealed record ActiveRelayNode(string RelayId, string IpAddress, int Port, string CountryCode, string CountryFlag, Guid UserId, DateTime LastSeen);
public sealed record MeshRelayInfoDto(string IpAddress, int Port, string RelayId, int LoadPercent, int PingMs, string CountryCode, string CountryFlag, bool IsFriend);
