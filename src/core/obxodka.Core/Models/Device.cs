namespace obxodka.Core.Models;

public sealed class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Hwid { get; set; }
    public string? Name { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public byte[]? EncryptedCertificate { get; set; }
    public string? CertThumbprint { get; set; }
    public DateTime LastActive { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
}
