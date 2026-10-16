using System.Management;

namespace obxodka.Maui.Platforms.Windows.Services;

public sealed class WindowsDeviceInfoService : IDeviceInfoService
{
    private static readonly Lazy<string> t_deviceId = new(QueryDeviceId);
    private static readonly Lazy<string> t_model = new(QueryModel);
    private static readonly Lazy<string> t_manufacturer = new(QueryManufacturer);
    private static readonly Lazy<AppDeviceIdiom> t_idiom = new(QueryIdiom);
    private static readonly Lazy<AppDeviceType> t_deviceType = new(QueryDeviceType);

    public string DeviceId => t_deviceId.Value;
    public string Model => t_model.Value;
    public string Manufacturer => t_manufacturer.Value;
    public string Name => Environment.MachineName;
    public string VersionString => Environment.OSVersion.VersionString;
    public string Platform => "Windows";
    public AppDeviceIdiom Idiom => t_idiom.Value;
    public AppDeviceType DeviceType => t_deviceType.Value;

    private static string QueryDeviceId()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct");
            foreach (var obj in searcher.Get().Cast<ManagementObject>())
            {
                return obj["UUID"]?.ToString() ?? string.Empty;
            }
        }
        catch { }
        return string.Empty;
    }

    private static string QueryModel()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem");
            foreach (var obj in searcher.Get().Cast<ManagementObject>())
            {
                return obj[nameof(Model)]?.ToString() ?? string.Empty;
            }
        }
        catch { }
        return string.Empty;
    }

    private static string QueryManufacturer()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer FROM Win32_ComputerSystem");
            foreach (var obj in searcher.Get().Cast<ManagementObject>())
            {
                return obj[nameof(Manufacturer)]?.ToString() ?? string.Empty;
            }
        }
        catch { }
        return string.Empty;
    }

    private static AppDeviceIdiom QueryIdiom()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
            foreach (var obj in searcher.Get().Cast<ManagementObject>())
            {
                var chassisTypes = obj["ChassisTypes"] as ushort[];
                if (chassisTypes?.Length > 0)
                {
                    return chassisTypes[0] switch
                    {
                        3 or 4 or 5 or 6 or 7 or 15 or 16 => AppDeviceIdiom.Desktop,
                        8 or 9 or 10 or 11 or 12 or 13 => AppDeviceIdiom.Phone,
                        14 => AppDeviceIdiom.Tablet,
                        _ => AppDeviceIdiom.Desktop
                    };
                }
            }
        }
        catch { }
        return AppDeviceIdiom.Desktop;
    }

    private static AppDeviceType QueryDeviceType()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem");
            foreach (var obj in searcher.Get().Cast<ManagementObject>())
            {
                var model = obj[nameof(Model)]?.ToString()?.ToLowerInvariant() ?? "";
                if (model.Contains("virtual") || model.Contains("vmware") || model.Contains("vbox"))
                {
                    return AppDeviceType.Virtual;
                }
            }
        }
        catch { }
        return AppDeviceType.Physical;
    }
}
