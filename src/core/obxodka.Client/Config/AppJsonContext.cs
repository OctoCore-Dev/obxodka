using obxodka.Models;

namespace obxodka.Config;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(EmailAuthRequest))]
[JsonSerializable(typeof(EmailVerifyRequest))]
[JsonSerializable(typeof(LoginResponse))]
[JsonSerializable(typeof(List<DeviceItem>))]
[JsonSerializable(typeof(DeviceItem))]
[JsonSerializable(typeof(UserSession))]
[JsonSerializable(typeof(AppInfoItem))]
[JsonSerializable(typeof(List<AppInfoItem>))]
[JsonSerializable(typeof(UserProfileResponse))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(VpnServerDto))]
[JsonSerializable(typeof(List<VpnServerDto>))]
[JsonSerializable(typeof(TrafficReportDto))]
[JsonSerializable(typeof(List<TrafficReportDto>))]
[JsonSerializable(typeof(SyncRequestDto))]
[JsonSerializable(typeof(MessageResponse))]
[JsonSerializable(typeof(PaymentLinkResponse))]
[JsonSerializable(typeof(object), TypeInfoPropertyName = "JsonObject")]
[JsonSerializable(typeof(CertHashResponse))]
[JsonSerializable(typeof(GooglePurchaseVerifyRequest))]
[JsonSerializable(typeof(GooglePurchaseVerifyResponse))]
[JsonSerializable(typeof(PostReviewRequest))]
[JsonSerializable(typeof(PostReviewResponse))]
[JsonSerializable(typeof(ReviewReplyDto))]
[JsonSerializable(typeof(ReviewDto))]
[JsonSerializable(typeof(List<ReviewDto>))]
[JsonSerializable(typeof(LikeResponse))]
[JsonSerializable(typeof(ReferralFriendDto))]
[JsonSerializable(typeof(List<ReferralFriendDto>))]
[JsonSerializable(typeof(ReferralCodeResponse))]
[JsonSerializable(typeof(ActivateReferralRequest))]
[JsonSerializable(typeof(IncidentReportDto))]
[JsonSerializable(typeof(TelemetryDto))]
internal sealed partial class AppJsonContext : JsonSerializerContext
{
}
