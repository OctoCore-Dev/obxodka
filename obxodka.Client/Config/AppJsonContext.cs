namespace obxodka.Config;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AuthRequest))]
[JsonSerializable(typeof(EmailAuthRequest))]
[JsonSerializable(typeof(EmailVerifyRequest))]
[JsonSerializable(typeof(RegisterRequest))]
[JsonSerializable(typeof(SendCodeRequest))]
[JsonSerializable(typeof(ResetPasswordRequest))]
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
[JsonSerializable(typeof(MessageResponse))]
[JsonSerializable(typeof(PaymentLinkResponse))]
[JsonSerializable(typeof(object), TypeInfoPropertyName = "JsonObject")]
[JsonSerializable(typeof(HydraConfig))]
[JsonSerializable(typeof(CertHashResponse))]
[JsonSerializable(typeof(GooglePurchaseVerifyRequest))]
[JsonSerializable(typeof(ReferralFriendDto))]
[JsonSerializable(typeof(List<ReferralFriendDto>))]
[JsonSerializable(typeof(ReferralCodeResponse))]
[JsonSerializable(typeof(ActivateReferralRequest))]
[JsonSerializable(typeof(ClaimRewardRequest))]
[JsonSerializable(typeof(ClaimRewardResponse))]
[JsonSerializable(typeof(MeshRelayInfo))]
[JsonSerializable(typeof(List<MeshRelayInfo>))]
internal sealed partial class AppJsonContext : JsonSerializerContext
{
}

