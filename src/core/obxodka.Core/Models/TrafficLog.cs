namespace obxodka.Core.Models;

public sealed class TrafficLog
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public long BytesDelta { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Protocol { get; set; } = "Octopus/gRPC";
}
