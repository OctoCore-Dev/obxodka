namespace obxodka.Models;
internal sealed class LoginResponse
{
    public string? Token { get; set; }
    public string? VpnLink { get; set; }
}
internal sealed class VpnStatusResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("seconds")]
    public long RemainingSeconds { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("isActive")]
    public bool IsActive { get; set; }
}
internal sealed class PaymentLinkResponse
{
    public string? Url { get; set; }
}
