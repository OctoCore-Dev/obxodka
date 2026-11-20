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


    private static string QueryModel() =>
        QueryWmiValue<object>("Win32_ComputerSystem", nameof(Model))?.ToString() ?? string.Empty;

    private static string QueryManufacturer() =>
        QueryWmiValue<object>("Win32_ComputerSystem", nameof(Manufacturer))?.ToString() ?? string.Empty;

    //получение первого значения свойства из wmi без sql-строк
    private static T? QueryWmiValue<T>(string wmiClass, string propertyName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new SelectQuery(wmiClass, null, [propertyName]));
            using var collection = searcher.Get();
            foreach (var obj in collection.Cast<ManagementObject>())
            {
                using (obj)
                {
                    if (obj[propertyName] is T value)
                    {
                        return value;
                    }
                }
            }
        }
        catch
        {
            // ignored
        }

        return default;
    }

    //получение uuid материнской платы или резервного machine guid из реестра
    private static string QueryDeviceId()
    {
        var uuid = QueryWmiValue<object>("Win32_ComputerSystemProduct", "UUID")?.ToString();
        if (!string.IsNullOrWhiteSpace(uuid) && uuid != "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF")
        {
            return uuid;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var guid = key?.GetValue("MachineGuid")?.ToString();
            if (!string.IsNullOrWhiteSpace(guid))
            {
                return guid;
            }
        }
        catch
        {
            // ignored
        }

        return Environment.MachineName;
    }

    private static AppDeviceIdiom QueryIdiom()
    {
        var chassisTypes = QueryWmiValue<ushort[]>("Win32_SystemEnclosure", "ChassisTypes");
        return chassisTypes?.Length > 0
            ? chassisTypes[0] switch
            {
                3 or 4 or 5 or 6 or 7 or 15 or 16 => AppDeviceIdiom.Desktop,
                8 or 9 or 10 or 11 or 12 or 13 => AppDeviceIdiom.Phone,
                14 => AppDeviceIdiom.Tablet,
                _ => AppDeviceIdiom.Desktop
            }
            : AppDeviceIdiom.Desktop;
    }

    private static AppDeviceType QueryDeviceType()
    {
        var model = QueryModel().ToLowerInvariant();
        return model.Contains("virtual") || model.Contains("vmware") || model.Contains("vbox") || model.Contains("qemu")
            ? AppDeviceType.Virtual
            : AppDeviceType.Physical;
    }
}
