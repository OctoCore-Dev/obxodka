namespace obxodka.Core.Models;

public sealed class ReferralActivation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReferrerUserId { get; set; }
    public Guid InvitedUserId { get; set; }
    public string InvitedEmailMasked { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public int BonusHours { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
