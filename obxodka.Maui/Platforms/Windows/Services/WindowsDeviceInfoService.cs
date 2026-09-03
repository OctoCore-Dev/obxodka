using System.Management;
using obxodka.Client.Platforms;

namespace obxodka.Maui.Platforms.Windows.Services;

public sealed class WindowsDeviceInfoService : IDeviceInfoService
{
    public string DeviceId
    {
        get
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
    }

    public string Model
    {
        get
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
    }

    public string Manufacturer
    {
        get
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
    }

    public string Name => Environment.MachineName;

    public string VersionString => Environment.OSVersion.VersionString;

    public string Platform => "Windows";

    public AppDeviceIdiom Idiom
    {
        get
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
    }

    public AppDeviceType DeviceType
    {
        get
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
}
