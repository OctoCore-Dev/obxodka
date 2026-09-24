using Microsoft.Win32;
using obxodka.Client.Platforms;

using System.Globalization;

namespace obxodka.Maui.Platforms.Windows.Services;

public sealed class WindowsPreferencesService(string subKey = "obxodka") : IPreferencesService
{
    private readonly string _subKey = $@"Software\{subKey}";

    public T GetValue<T>(string key, T defaultValue = default!)
    {
        using var regKey = Registry.CurrentUser.CreateSubKey(_subKey);
        var value = regKey?.GetValue(key);
        if (value == null)
        {
            return defaultValue;
        }

        try
        {
            if (typeof(T) == typeof(string))
            {
                return (T)(object)(value?.ToString() ?? string.Empty);
            }

            var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            return (T)Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        }
        catch
        {
            return defaultValue;
        }
    }

    public T Get<T>(string key, T defaultValue = default!) => GetValue(key, defaultValue);

    public void SetValue<T>(string key, T value)
    {
        using var regKey = Registry.CurrentUser.CreateSubKey(_subKey);
        regKey?.SetValue(key, value?.ToString() ?? string.Empty);
    }

    public void Set<T>(string key, T value) => SetValue(key, value);

    public void Remove(string key)
    {
        using var regKey = Registry.CurrentUser.OpenSubKey(_subKey, true);
        regKey?.DeleteValue(key, false);
    }

    public void Clear()
    {
        using var regKey = Registry.CurrentUser.OpenSubKey(_subKey, true);
        if (regKey != null)
        {
            foreach (var valueName in regKey.GetValueNames())
            {
                regKey.DeleteValue(valueName, false);
            }
        }
    }
}
