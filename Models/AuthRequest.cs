namespace obxodka.Models;
internal sealed class AuthRequest
{
    public required string Email { get; init; }
    public required string Password { get; init; }
    public required string Hwid { get; init; }
    public string? DeviceName { get; set; }
}