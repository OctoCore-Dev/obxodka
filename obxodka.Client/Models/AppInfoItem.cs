namespace obxodka.Client.Models;

public class AppInfoItem
{
    public string Name { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;
    public bool IsBypassed { get; set; }
    public string? IconPath { get; set; }
    public object? IconSource => IconPath is { Length: > 0 } ? IconPath : null;

    public AppInfoItem() { }

    public AppInfoItem(string name, string packageName, bool isBypassed = false, string? iconPath = null)
    {
        Name = name;
        PackageName = packageName;
        IsBypassed = isBypassed;
        IconPath = iconPath;
    }
}