namespace obxodka.Models;

public sealed class UserSession
{
    public string? Email { get; set; }
    public string? Password { get; set; }
    public string? JwtToken { get; set; }
    public bool IsLoggedIn { get; set; }
    public string? VpnConfig { get; set; }
    public DateTime? SubscriptionUntil { get; set; }
    public long BalanceSeconds { get; set; }
}
