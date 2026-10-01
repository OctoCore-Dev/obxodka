namespace obxodka.Maui.Services;

public sealed class MauiPreferencesService : IPreferencesService
{
    public T GetValue<T>(string key, T defaultValue = default!)
    {
        if (Preferences.Default.ContainsKey(key))
        {
            return Preferences.Default.Get(key, defaultValue);
        }

#if WINDOWS
        try
        {
            using var regKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\obxodka");
            var val = regKey?.GetValue(key);
            if (val != null)
            {
                if (typeof(T) == typeof(string))
                {
                    var strVal = (T)(object)val.ToString()!;
                    Preferences.Default.Set(key, strVal);
                    return strVal;
                }

                var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
                var converted = (T)Convert.ChangeType(val, targetType, CultureInfo.InvariantCulture);
                Preferences.Default.Set(key, converted);
                return converted;
            }
        }
        catch { }
#endif

        return defaultValue;
    }

    public T Get<T>(string key, T defaultValue = default!) => GetValue(key, defaultValue);

    public void SetValue<T>(string key, T value)
    {
        Preferences.Default.Set(key, value);
#if WINDOWS
        try
        {
            using var regKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\obxodka");
            regKey?.SetValue(key, value?.ToString() ?? string.Empty);
        }
        catch { }
#endif
    }

    public void Set<T>(string key, T value) => SetValue(key, value);

    public void Remove(string key)
    {
        Preferences.Default.Remove(key);
#if WINDOWS
        try
        {
            using var regKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\obxodka", true);
            regKey?.DeleteValue(key, false);
        }
        catch { }
#endif
    }

    public void Clear()
    {
        Preferences.Default.Clear();
#if WINDOWS
        try
        {
            using var regKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\obxodka", true);
            if (regKey != null)
            {
                foreach (var v in regKey.GetValueNames())
                {
                    regKey.DeleteValue(v, false);
                }
            }
        }
        catch { }
#endif
    }
}
