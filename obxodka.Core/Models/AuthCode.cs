namespace obxodka.Core.Models;

public sealed class AuthCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Email { get; set; }
    public required string Code { get; set; }
    public required string Purpose { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddMinutes(10);
    public bool IsUsed { get; set; }
}
