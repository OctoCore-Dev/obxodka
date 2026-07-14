namespace obxodka.Models;

public partial class AppInfoItem : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBypassed { get; set; }

    public string? IconPath { get; set; }

    public ImageSource? IconSource => IconPath != null ? ImageSource.FromFile(IconPath) : null;
}
