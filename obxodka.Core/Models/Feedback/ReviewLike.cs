namespace obxodka.Core.Models.Feedback;

public sealed class ReviewLike
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReviewId { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
