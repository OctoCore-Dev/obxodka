using System.Globalization;
using obxodka.Client.Platforms;

namespace obxodka.Client;

public static class Preferences
{
    public static IPreferencesService Default => PlatformServices.Preferences;

    public static T Get<T>(string key, T defaultValue = default!) => PlatformServices.Preferences.GetValue(key, defaultValue);

    public static void Set<T>(string key, T value) => PlatformServices.Preferences.SetValue(key, value);

    public static T GetValue<T>(string key, T defaultValue = default!) => PlatformServices.Preferences.GetValue(key, defaultValue);

    public static void SetValue<T>(string key, T value) => PlatformServices.Preferences.SetValue(key, value);

    public static void Remove(string key) => PlatformServices.Preferences.Remove(key);

    public static void Clear() => PlatformServices.Preferences.Clear();

    public static bool ContainsKey(string key)
    {
        return PlatformServices.Preferences is DefaultPreferencesService defaultPrefs
            ? defaultPrefs.ContainsKey(key)
            : !EqualityComparer<string?>.Default.Equals(PlatformServices.Preferences.GetValue<string?>(key, null), null);
    }
}

public static class SecureStorage
{
    public static ISecureStorageService Default => PlatformServices.SecureStorage;

    public static Task<string?> GetAsync(string key) => PlatformServices.SecureStorage.GetAsync(key);

    public static Task SetAsync(string key, string value) => PlatformServices.SecureStorage.SetAsync(key, value);

    public static bool Remove(string key) => PlatformServices.SecureStorage.Remove(key);

    public static void RemoveAll() => PlatformServices.SecureStorage.RemoveAll();
}

public static class Connectivity
{
    public static IConnectivityService Current => PlatformServices.Connectivity;

    public static AppNetworkAccess NetworkAccess => PlatformServices.Connectivity.NetworkAccess;

    public static event EventHandler<AppConnectivityChangedEventArgs> ConnectivityChanged
    {
        add => PlatformServices.Connectivity.ConnectivityChanged += value;
        remove => PlatformServices.Connectivity.ConnectivityChanged -= value;
    }

    public static Task<AppConnectionProfile> GetConnectionProfileAsync() =>
        PlatformServices.Connectivity.GetConnectionProfileAsync();
}

public static class MainThread
{
    public static bool IsMainThread => PlatformServices.MainThread.IsMainThread;

    public static void BeginInvokeOnMainThread(Action action) =>
        PlatformServices.MainThread.BeginInvokeOnMainThread(action);

    public static Task InvokeOnMainThreadAsync(Action action) =>
        PlatformServices.MainThread.InvokeOnMainThreadAsync(action);

    public static Task<T> InvokeOnMainThreadAsync<T>(Func<Task<T>> func) =>
        PlatformServices.MainThread.InvokeOnMainThreadAsync(func);

    public static Task<T> InvokeOnMainThreadAsync<T>(Func<T> func) =>
        PlatformServices.MainThread.InvokeOnMainThreadAsync(() => Task.FromResult(func()));
}

public static class DeviceInfo
{
    public static IDeviceInfoService Current => PlatformServices.DeviceInfo;
    public static string Name => PlatformServices.DeviceInfo.Name;
    public static string Model => PlatformServices.DeviceInfo.Model;
    public static string Manufacturer => PlatformServices.DeviceInfo.Manufacturer;
    public static string VersionString => PlatformServices.DeviceInfo.VersionString;
    public static string Platform => PlatformServices.DeviceInfo.Platform;
    public static AppDeviceIdiom Idiom => PlatformServices.DeviceInfo.Idiom;
    public static AppDeviceType DeviceType => PlatformServices.DeviceInfo.DeviceType;
}
