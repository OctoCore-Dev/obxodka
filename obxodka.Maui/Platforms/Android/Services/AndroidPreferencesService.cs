using Android.Content;
using obxodka.Client.Platforms;

using System.Globalization;

namespace obxodka.Maui.Platforms.Android.Services;

public sealed class AndroidPreferencesService(Context context, string name = "obxodka_prefs") : IPreferencesService
{
    private readonly ISharedPreferences _prefs = context.GetSharedPreferences(name, FileCreationMode.Private)!;

    public T GetValue<T>(string key, T defaultValue = default!)
    {
        if (typeof(T) == typeof(string))
        {
            var value = _prefs.GetString(key, null);
            return value != null ? (T)(object)value : defaultValue;
        }
        if (typeof(T) == typeof(int))
        {
            var defInt = defaultValue is int i ? i : 0;
            return (T)(object)_prefs.GetInt(key, defInt);
        }
        if (typeof(T) == typeof(bool))
        {
            var defBool = defaultValue is bool b && b;
            return (T)(object)_prefs.GetBoolean(key, defBool);
        }
        if (typeof(T) == typeof(long))
        {
            var defLong = defaultValue is long l ? l : 0L;
            return (T)(object)_prefs.GetLong(key, defLong);
        }
        if (typeof(T) == typeof(float))
        {
            var defFloat = defaultValue is float f ? f : 0f;
            return (T)(object)_prefs.GetFloat(key, defFloat);
        }

        var str = _prefs.GetString(key, null);
        if (str is null)
        {
            return defaultValue;
        }

        try
        {
            var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            return (T)Convert.ChangeType(str, targetType, CultureInfo.InvariantCulture);
        }
        catch
        {
            return defaultValue;
        }
    }

    public T Get<T>(string key, T defaultValue = default!) => GetValue(key, defaultValue);

    public void SetValue<T>(string key, T value)
    {
        using var editor = _prefs.Edit()!;
        _ = value is string s
            ? editor.PutString(key, s)
            : value is int i
            ? editor.PutInt(key, i)
            : value is bool b
                ? editor.PutBoolean(key, b)
                : value is long l
                ? editor.PutLong(key, l)
                : value is float f ? editor.PutFloat(key, f) : value is null ? editor.Remove(key) : editor.PutString(key, value.ToString());

        editor.Apply();
    }

    public void Set<T>(string key, T value) => SetValue(key, value);

    public void Remove(string key)
    {
        using var editor = _prefs.Edit()!;
        _ = editor.Remove(key);
        editor.Apply();
    }

    public void Clear()
    {
        using var editor = _prefs.Edit()!;
        _ = editor.Clear();
        editor.Apply();
    }
}
