namespace obxodka.Models;
public sealed class UpdateInfo
{
    public string Version { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public string WindowsUrl { get; set; } = string.Empty;
    public string AndroidUrl { get; set; } = string.Empty;
    public bool IsCritical { get; set; } = false;
}