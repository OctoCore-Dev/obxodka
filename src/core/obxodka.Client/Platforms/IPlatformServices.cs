namespace obxodka.Client.Platforms;

//интерфейс сохранения пользовательских настроек приложения
public interface IPreferencesService
{
    T Get<T>(string key, T defaultValue = default!);
    void Set<T>(string key, T value);
    bool ContainsKey(string key);
    void Remove(string key);
    void Clear();

    T GetValue<T>(string key, T defaultValue = default!) => Get(key, defaultValue);
    void SetValue<T>(string key, T value) => Set(key, value);
}

//интерфейс защищенного хранилища паролей и ключей
public interface ISecureStorageService
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    bool Remove(string key);
    void RemoveAll();
}

//интерфейс мониторинга состояния сети и подключения к интернету
public interface IConnectivityService
{
    AppNetworkAccess NetworkAccess { get; }
    event EventHandler<AppConnectivityChangedEventArgs> ConnectivityChanged;
    Task<AppConnectionProfile> GetConnectionProfileAsync();
}

//интерфейс информации об устройстве и платформе
public interface IDeviceInfoService
{
    string DeviceId { get; }
    string Model { get; }
    string Manufacturer { get; }
    string Name { get; }
    string VersionString { get; }
    string Platform { get; }
    AppDeviceIdiom Idiom { get; }
    AppDeviceType DeviceType { get; }
}

//интерфейс диспетчеризации вызовов в главном ui потоке
public interface IMainThreadService
{
    bool IsMainThread { get; }
    void BeginInvokeOnMainThread(Action action);
    Task InvokeOnMainThreadAsync(Action action);
    Task<T> InvokeOnMainThreadAsync<T>(Func<Task<T>> func);
}

//интерфейс аудита корневых сертификатов устройства на предмет mitm перехвата
public interface ICertificateAuditService
{
    Task<CertificateAuditResult> CheckCertificatesAsync();
    Task OpenCertificateSettingsAsync();
    Task<bool> TryRemoveUserCertificateAsync(string thumbprint);
}

//интерфейс системных уведомлений
public interface INotificationService
{
    void ShowUnexpectedDisconnectNotification();
    void RequestReconnect();
    event Action? ReconnectRequested;
}

//результат проверки сертификатов
public sealed record CertificateAuditResult(bool HasUntrustedRoot, string? CertificateName, string? Thumbprint, string? Details);

//событие изменения подключения к сети
public sealed class AppConnectivityChangedEventArgs(AppNetworkAccess networkAccess, IEnumerable<AppConnectionProfile> connectionProfiles)
    : EventArgs
{
    public AppNetworkAccess NetworkAccess { get; } = networkAccess;
    public IEnumerable<AppConnectionProfile> ConnectionProfiles { get; } = connectionProfiles;
}

//профиль сетевого адаптера
public sealed record AppConnectionProfile(NetworkConnectionType ConnectionType, AppNetworkAccess NetworkAccess);

public enum NetworkConnectionType { Unknown, Cellular, Wifi, Bluetooth, Ethernet, Other }

public enum AppNetworkAccess { Unknown, None, Local, ConstrainedInternet, Internet }

public enum AppDeviceIdiom { Unknown, Phone, Tablet, Desktop, Tv, Watch, Car }

public enum AppDeviceType { Unknown, Physical, Virtual }
