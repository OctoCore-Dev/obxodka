namespace obxodka.Core.Models.Support;

public enum BugStatus
{
    New,
    InProgress,
    Resolved,
    Rejected
}

public sealed class BugReport
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [MaxLength(2000)]
    public string Description { get; set; } = string.Empty;

    public BugStatus Status { get; set; } = BugStatus.New;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string? ImagePath { get; set; }
}
