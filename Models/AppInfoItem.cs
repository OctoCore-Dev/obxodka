namespace obxodka.Models;

public sealed partial class AppInfoItem : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBypassed { get; set; }

    public string? IconPath
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                IconSource = value is { Length: > 0 } ? ImageSource.FromFile(value) : null;
            }
        }
    }

    [ObservableProperty]
    public partial ImageSource? IconSource { get; set; }
}
