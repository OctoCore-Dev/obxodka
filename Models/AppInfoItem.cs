namespace obxodka.Models;

public class AppInfoItem
{
    public string Name { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;
    public bool IsBypassed { get; set; }
    public ImageSource? Icon { get; set; }
}
