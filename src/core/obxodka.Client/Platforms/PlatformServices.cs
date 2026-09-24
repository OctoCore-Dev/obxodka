namespace obxodka.Client.Platforms;

public static class PlatformServices
{
    private static IPreferencesService t_preferences = new DefaultPreferencesService();
    private static ISecureStorageService t_secureStorage = new DefaultSecureStorageService();
    private static IConnectivityService t_connectivity = new DefaultConnectivityService();
    private static IMainThreadService t_mainThread = new DefaultMainThreadService();
    private static IDeviceInfoService t_deviceInfo = new DefaultDeviceInfoService();
    private static ICertificateAuditService t_certificateAudit = new DefaultCertificateAuditService();

    public static IPreferencesService Preferences
    {
        get => t_preferences;
        set => t_preferences = value ?? new DefaultPreferencesService();
    }

    public static ISecureStorageService SecureStorage
    {
        get => t_secureStorage;
        set => t_secureStorage = value ?? new DefaultSecureStorageService();
    }

    public static IConnectivityService Connectivity
    {
        get => t_connectivity;
        set => t_connectivity = value ?? new DefaultConnectivityService();
    }

    public static IMainThreadService MainThread
    {
        get => t_mainThread;
        set => t_mainThread = value ?? new DefaultMainThreadService();
    }

    public static IDeviceInfoService DeviceInfo
    {
        get => t_deviceInfo;
        set => t_deviceInfo = value ?? new DefaultDeviceInfoService();
    }

    public static ICertificateAuditService CertificateAudit
    {
        get => t_certificateAudit;
        set => t_certificateAudit = value ?? new DefaultCertificateAuditService();
    }

    public static void Init(
        IPreferencesService? preferences = null,
        ISecureStorageService? secureStorage = null,
        IConnectivityService? connectivity = null,
        IMainThreadService? mainThread = null,
        IDeviceInfoService? deviceInfo = null,
        ICertificateAuditService? certificateAudit = null)
    {
        if (preferences is not null)
        {
            t_preferences = preferences;
        }

        if (secureStorage is not null)
        {
            t_secureStorage = secureStorage;
        }

        if (connectivity is not null)
        {
            t_connectivity = connectivity;
        }

        if (mainThread is not null)
        {
            t_mainThread = mainThread;
        }

        if (deviceInfo is not null)
        {
            t_deviceInfo = deviceInfo;
        }

        if (certificateAudit is not null)
        {
            t_certificateAudit = certificateAudit;
        }
    }
}

public sealed class DefaultPreferencesService : IPreferencesService
{
    private readonly ConcurrentDictionary<string, object?> _cache = new();

    public T GetValue<T>(string key, T defaultValue = default!)
    {
        if (_cache.TryGetValue(key, out var val) && val is not null)
        {
            if (val is T exact)
            {
                return exact;
            }

            try
            {
                var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
                return (T)Convert.ChangeType(val, targetType, CultureInfo.InvariantCulture);
            }
            catch
            {
                return defaultValue;
            }
        }
        return defaultValue;
    }

    public void SetValue<T>(string key, T value)
    {
        if (value is null)
        {
            _ = _cache.TryRemove(key, out _);
        }
        else
        {
            _cache[key] = value;
        }
    }

    public T Get<T>(string key, T defaultValue = default!) => GetValue(key, defaultValue);
    public void Set<T>(string key, T value) => SetValue(key, value);

    public void Remove(string key) => _cache.TryRemove(key, out _);

    public void Clear() => _cache.Clear();

    public bool ContainsKey(string key) => _cache.ContainsKey(key);
}

public sealed class DefaultSecureStorageService : ISecureStorageService
{
    private readonly ConcurrentDictionary<string, string> _storage = new();

    public Task<string?> GetAsync(string key)
    {
        _ = _storage.TryGetValue(key, out var val);
        return Task.FromResult(val);
    }

    public Task SetAsync(string key, string value)
    {
        _storage[key] = value;
        return Task.CompletedTask;
    }

    public bool Remove(string key) => _storage.TryRemove(key, out _);

    public void RemoveAll() => _storage.Clear();
}

public sealed class DefaultConnectivityService : IConnectivityService
{
    public AppNetworkAccess NetworkAccess => AppNetworkAccess.Internet;

    public event EventHandler<AppConnectivityChangedEventArgs>? ConnectivityChanged
    {
        add { }
        remove { }
    }

    public Task<AppConnectionProfile> GetConnectionProfileAsync() =>
        Task.FromResult(new AppConnectionProfile(NetworkConnectionType.Ethernet, AppNetworkAccess.Internet));
}

public sealed class DefaultMainThreadService : IMainThreadService
{
    public bool IsMainThread => true;

    public void BeginInvokeOnMainThread(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DefaultMainThreadService] Unhandled action error: {ex}");
        }
    }

    public Task InvokeOnMainThreadAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public Task<T> InvokeOnMainThreadAsync<T>(Func<Task<T>> func) => func();
}

public sealed class DefaultDeviceInfoService : IDeviceInfoService
{
    public string DeviceId => Environment.MachineName;
    public string Model => Environment.OSVersion.VersionString;
    public string Manufacturer => "Generic";
    public string Name => Environment.MachineName;
    public string VersionString => Environment.Version.ToString();
    public string Platform => Environment.OSVersion.Platform.ToString();
    public AppDeviceIdiom Idiom => AppDeviceIdiom.Desktop;
    public AppDeviceType DeviceType => AppDeviceType.Physical;
}

public sealed class DefaultCertificateAuditService : ICertificateAuditService
{
    public Task<CertificateAuditResult> CheckCertificatesAsync() =>
        Task.FromResult(new CertificateAuditResult(false, null, null, null));

    public Task OpenCertificateSettingsAsync() => Task.CompletedTask;

    public Task<bool> TryRemoveUserCertificateAsync(string thumbprint) => Task.FromResult(false);
}
