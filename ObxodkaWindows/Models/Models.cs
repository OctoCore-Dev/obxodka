namespace ObxodkaWindows.Models
{
    public class LoginResponse
    {
        public string? Token { get; set; }
        public string? VpnLink { get; set; }
    }

    public class VpnStatusResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("seconds")]
        public long RemainingSeconds { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("isActive")]
        public bool IsActive { get; set; }
    }
}