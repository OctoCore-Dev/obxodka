namespace obxodka.Client.Platforms;

//статический доступ к платформа-зависимым службам с реализациями по умолчанию
public static class PlatformServices
{
    private static IPreferencesService s_preferences = new DefaultPreferencesService();
    private static ISecureStorageService s_secureStorage = new DefaultSecureStorageService();
    private static IConnectivityService s_connectivity = new DefaultConnectivityService();
    private static IMainThreadService s_mainThread = new DefaultMainThreadService();
    private static IDeviceInfoService s_deviceInfo = new DefaultDeviceInfoService();
    private static ICertificateAuditService s_certificateAudit = new DefaultCertificateAuditService();
    private static INotificationService s_notification = new DefaultNotificationService();

    //служба пользовательских настроек
    public static IPreferencesService Preferences
    {
        get => s_preferences;
        set => s_preferences = value ?? new DefaultPreferencesService();
    }

    //служба защищенного хранилища
    public static ISecureStorageService SecureStorage
    {
        get => s_secureStorage;
        set => s_secureStorage = value ?? new DefaultSecureStorageService();
    }

    //служба состояния сети
    public static IConnectivityService Connectivity
    {
        get => s_connectivity;
        set => s_connectivity = value ?? new DefaultConnectivityService();
    }

    //служба главного потока
    public static IMainThreadService MainThread
    {
        get => s_mainThread;
        set => s_mainThread = value ?? new DefaultMainThreadService();
    }

    //служба информации об устройстве
    public static IDeviceInfoService DeviceInfo
    {
        get => s_deviceInfo;
        set => s_deviceInfo = value ?? new DefaultDeviceInfoService();
    }

    //служба аудита сертификатов
    public static ICertificateAuditService CertificateAudit
    {
        get => s_certificateAudit;
        set => s_certificateAudit = value ?? new DefaultCertificateAuditService();
    }

    //служба уведомлений
    public static INotificationService Notification
    {
        get => s_notification;
        set => s_notification = value ?? new DefaultNotificationService();
    }

    //инициализация платформенных служб при запуске
    public static void Init(
        IPreferencesService? preferences = null,
        ISecureStorageService? secureStorage = null,
        IConnectivityService? connectivity = null,
        IMainThreadService? mainThread = null,
        IDeviceInfoService? deviceInfo = null,
        ICertificateAuditService? certificateAudit = null,
        INotificationService? notification = null)
    {
        if (preferences is not null)
        {
            s_preferences = preferences;
        }

        if (secureStorage is not null)
        {
            s_secureStorage = secureStorage;
        }

        if (connectivity is not null)
        {
            s_connectivity = connectivity;
        }

        if (mainThread is not null)
        {
            s_mainThread = mainThread;
        }

        if (deviceInfo is not null)
        {
            s_deviceInfo = deviceInfo;
        }

        if (certificateAudit is not null)
        {
            s_certificateAudit = certificateAudit;
        }

        if (notification is not null)
        {
            s_notification = notification;
        }
    }
}

//реализация настроек в оперативной памяти для тестов и консоли
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

//реализация защищенного хранилища в памяти для тестов
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

//реализация состояния сети по умолчанию
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

//реализация главного потока с синхронным выполнением
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

//реализация информации об устройстве по данным среды
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

//реализация аудита сертификатов по умолчанию
public sealed class DefaultCertificateAuditService : ICertificateAuditService
{
    public Task<CertificateAuditResult> CheckCertificatesAsync() =>
        Task.FromResult(new CertificateAuditResult(false, null, null, null));

    public Task OpenCertificateSettingsAsync() => Task.CompletedTask;

    public Task<bool> TryRemoveUserCertificateAsync(string thumbprint) => Task.FromResult(false);
}

//реализация системных уведомлений по умолчанию
public sealed class DefaultNotificationService : INotificationService
{
    public void ShowUnexpectedDisconnectNotification() { }

    public void RequestReconnect() { }

    public event Action? ReconnectRequested
    {
        add { }
        remove { }
    }
}
