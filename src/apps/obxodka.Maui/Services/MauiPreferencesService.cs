namespace obxodka.Maui.Services;

//адаптер настроек приложения на базе кроссплатформенного хранилища maui
public sealed class MauiPreferencesService : IPreferencesService
{
    //получение значения настройки с поддержкой примитивов и перечислений
    public T Get<T>(string key, T defaultValue = default!)
    {
        if (!Preferences.Default.ContainsKey(key))
        {
            return defaultValue;
        }

        var targetType = typeof(T);
        if (targetType.IsEnum)
        {
            var rawInt = Preferences.Default.Get(key, Convert.ToInt32(defaultValue, CultureInfo.InvariantCulture));
            return (T)Enum.ToObject(targetType, rawInt);
        }

        return Preferences.Default.Get(key, defaultValue);
    }

    //сохранение значения настройки в платформенное хранилище
    public void Set<T>(string key, T value)
    {
        if (value is null)
        {
            Preferences.Default.Remove(key);
            return;
        }

        if (typeof(T).IsEnum)
        {
            Preferences.Default.Set(key, Convert.ToInt32(value, CultureInfo.InvariantCulture));
            return;
        }

        Preferences.Default.Set(key, value);
    }

    //проверка наличия ключа в настройках
    public bool ContainsKey(string key) => Preferences.Default.ContainsKey(key);

    //удаление ключа из настроек
    public void Remove(string key) => Preferences.Default.Remove(key);

    //полная очистка всех сохраненных настроек
    public void Clear() => Preferences.Default.Clear();
}
