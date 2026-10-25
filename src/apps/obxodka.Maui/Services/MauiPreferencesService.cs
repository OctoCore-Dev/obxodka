namespace obxodka.Maui.Services;

public sealed class MauiPreferencesService : IPreferencesService
{
    public T GetValue<T>(string key, T defaultValue = default!)
    {
        try
        {
            if (Preferences.Default.ContainsKey(key))
            {
                return Preferences.Default.Get(key, defaultValue);
            }
        }
        catch { }

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
                    try
                    {
                        Preferences.Default.Set(key, strVal);
                    }
                    catch { }
                    return strVal;
                }

                var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
                var converted = (T)Convert.ChangeType(val, targetType, CultureInfo.InvariantCulture);
                try
                {
                    Preferences.Default.Set(key, converted);
                }
                catch { }
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
        try
        {
            Preferences.Default.Set(key, value);
        }
        catch { }
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
        try
        {
            Preferences.Default.Remove(key);
        }
        catch { }
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
        try
        {
            Preferences.Default.Clear();
        }
        catch { }
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
