namespace obxodka.Core.Models.Feedback;

public sealed class Review
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;
    [Required]
    [MaxLength(1000)]
    public string Text { get; set; } = string.Empty;
    public int? Rating { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? ParentId { get; set; }
    [ForeignKey(nameof(ParentId))]
    public Review? Parent { get; set; }
    public ICollection<Review> Replies { get; set; } = [];
    public ICollection<ReviewLike> ReviewLikes { get; set; } = [];
    [NotMapped]
    public int LikesCount => ReviewLikes?.Count ?? 0;
}
