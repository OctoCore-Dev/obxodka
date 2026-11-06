using System.Management;
using Microsoft.Win32;

namespace obxodka.Platforms.Windows;

//сбор аппаратных характеристик и уникального идентификатора устройства на платформе windows
public sealed class WindowsDeviceInfoService : IDeviceInfoService
{
    private static readonly Lazy<string> s_deviceId = new(QueryDeviceId);
    private static readonly Lazy<string> s_model = new(QueryModel);
    private static readonly Lazy<string> s_manufacturer = new(QueryManufacturer);
    private static readonly Lazy<AppDeviceIdiom> s_idiom = new(QueryIdiom);
    private static readonly Lazy<AppDeviceType> s_deviceType = new(QueryDeviceType);

    public string DeviceId => s_deviceId.Value;
    public string Model => s_model.Value;
    public string Manufacturer => s_manufacturer.Value;
    public string Name => Environment.MachineName;
    public string VersionString => Environment.OSVersion.VersionString;
    public string Platform => "Windows";
    public AppDeviceIdiom Idiom => s_idiom.Value;
    public AppDeviceType DeviceType => s_deviceType.Value;

    //получение uuid материнской платы или резервного machine guid из реестра
    private static string QueryDeviceId()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct");
            using var collection = searcher.Get();
            foreach (var obj in collection.Cast<ManagementObject>())
            {
                using (obj)
                {
                    var uuid = obj["UUID"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(uuid) && uuid != "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF")
                    {
                        return uuid;
                    }
                }
            }
        }
        catch { }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var guid = key?.GetValue("MachineGuid")?.ToString();
            if (!string.IsNullOrWhiteSpace(guid))
            {
                return guid;
            }
        }
        catch { }

        return Environment.MachineName;
    }

    private static string QueryModel()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem");
            using var collection = searcher.Get();
            foreach (var obj in collection.Cast<ManagementObject>())
            {
                using (obj)
                {
                    return obj[nameof(Model)]?.ToString() ?? string.Empty;
                }
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
            using var collection = searcher.Get();
            foreach (var obj in collection.Cast<ManagementObject>())
            {
                using (obj)
                {
                    return obj[nameof(Manufacturer)]?.ToString() ?? string.Empty;
                }
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
            using var collection = searcher.Get();
            foreach (var obj in collection.Cast<ManagementObject>())
            {
                using (obj)
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
        }
        catch { }
        return AppDeviceIdiom.Desktop;
    }

    private static AppDeviceType QueryDeviceType()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem");
            using var collection = searcher.Get();
            foreach (var obj in collection.Cast<ManagementObject>())
            {
                using (obj)
                {
                    var model = obj[nameof(Model)]?.ToString()?.ToLowerInvariant() ?? string.Empty;
                    if (model.Contains("virtual") || model.Contains("vmware") || model.Contains("vbox") || model.Contains("qemu"))
                    {
                        return AppDeviceType.Virtual;
                    }
                }
            }
        }
        catch { }
        return AppDeviceType.Physical;
    }
}
