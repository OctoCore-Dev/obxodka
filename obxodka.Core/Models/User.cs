namespace obxodka.Core.Models;

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public string? CertThumbprint { get; set; }
    public byte[]? ClientCertificate { get; set; }

    public long BalanceSeconds { get; set; }
    public bool IsFreeUsed { get; set; }
    public DateTime? SubscriptionUntil { get; set; }
    public bool HasActiveSubscription => SubscriptionUntil.HasValue && SubscriptionUntil.Value > DateTime.UtcNow;

    public bool IsVpnActive { get; set; }
    public DateTime LastPing { get; set; } = DateTime.UtcNow;

    public long LastTotalTraffic { get; set; }
    public long TotalBytesUsed { get; set; }

    public bool IsDeleted { get; set; }
    public List<Device> Devices { get; set; } = [];

    public string? ReferralCode { get; set; }
    public string? ActivatedPromoCodes { get; set; } = "";
    public int OwnCodeActivatedCount { get; set; }

    public string? Achievements { get; set; } = "";
    public string? PinnedAchievements { get; set; } = "";

    public long TotalSecondsUsed { get; set; }
    public long TotalMeshBytesRelayed { get; set; }
    public Guid? ReferredById { get; set; }
}
