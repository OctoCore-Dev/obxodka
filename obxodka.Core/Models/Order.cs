namespace obxodka.Core.Models;

public sealed class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "RUB";
    public string Status { get; set; } = "Pending";
    public string? ExternalId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int HoursAdded { get; set; }
}
