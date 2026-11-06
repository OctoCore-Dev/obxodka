namespace obxodka.Client.Platforms;

//интерфейс сохранения пользовательских настроек приложения
public interface IPreferencesService
{
    public T Get<T>(string key, T defaultValue = default!);
    public void Set<T>(string key, T value);
    public bool ContainsKey(string key);
    public void Remove(string key);
    public void Clear();

    public T GetValue<T>(string key, T defaultValue = default!) => Get(key, defaultValue);
    public void SetValue<T>(string key, T value) => Set(key, value);
}

//интерфейс защищенного хранилища паролей и ключей
public interface ISecureStorageService
{
    public Task<string?> GetAsync(string key);
    public Task SetAsync(string key, string value);
    public bool Remove(string key);
    public void RemoveAll();
}

//интерфейс мониторинга состояния сети и подключения к интернету
public interface IConnectivityService
{
    public AppNetworkAccess NetworkAccess { get; }
    public event EventHandler<AppConnectivityChangedEventArgs> ConnectivityChanged;
    public Task<AppConnectionProfile> GetConnectionProfileAsync();
}

//интерфейс информации об устройстве и платформе
public interface IDeviceInfoService
{
    public string DeviceId { get; }
    public string Model { get; }
    public string Manufacturer { get; }
    public string Name { get; }
    public string VersionString { get; }
    public string Platform { get; }
    public AppDeviceIdiom Idiom { get; }
    public AppDeviceType DeviceType { get; }
}

//интерфейс диспетчеризации вызовов в главном ui потоке
public interface IMainThreadService
{
    public bool IsMainThread { get; }
    public void BeginInvokeOnMainThread(Action action);
    public Task InvokeOnMainThreadAsync(Action action);
    public Task<T> InvokeOnMainThreadAsync<T>(Func<Task<T>> func);
}

//интерфейс служб геолокации
public interface IGeolocationService
{
    public Task<Location?> GetLastKnownLocationAsync();
    public Task<Location?> GetLocationAsync(GeolocationRequest request, CancellationToken cancellationToken = default);
    public event EventHandler<GeolocationChangedEventArgs> LocationChanged;
}

//интерфейс буфера обмена
public interface IClipboardService
{
    public Task<string?> GetTextAsync();
    public Task SetTextAsync(string text);
    public bool HasText { get; }
}

//интерфейс быстрых действий приложения
public interface IAppActionsService
{
    public Task SetAsync(IEnumerable<AppAction> actions);
    public event EventHandler<AppActionEventArgs> OnAction;
}

//интерфейс аудита корневых сертификатов устройства на предмет mitm перехвата
public interface ICertificateAuditService
{
    public Task<CertificateAuditResult> CheckCertificatesAsync();
    public Task OpenCertificateSettingsAsync();
    public Task<bool> TryRemoveUserCertificateAsync(string thumbprint);
}

//интерфейс системных уведомлений
public interface INotificationService
{
    public void ShowUnexpectedDisconnectNotification();
    public void RequestReconnect();
    public event Action? ReconnectRequested;
}

//результат проверки сертификатов
public sealed record CertificateAuditResult(bool HasUntrustedRoot, string? CertificateName, string? Thumbprint, string? Details);

//событие изменения подключения к сети
public sealed class AppConnectivityChangedEventArgs(AppNetworkAccess networkAccess, IEnumerable<AppConnectionProfile> connectionProfiles) : EventArgs
{
    public AppNetworkAccess NetworkAccess { get; } = networkAccess;
    public IEnumerable<AppConnectionProfile> ConnectionProfiles { get; } = connectionProfiles;
}

//профиль сетевого адаптера
public sealed record AppConnectionProfile(NetworkConnectionType ConnectionType, AppNetworkAccess NetworkAccess);
public enum NetworkConnectionType { Unknown, Cellular, Wifi, Bluetooth, Ethernet, Other }
public enum AppNetworkAccess { Unknown, None, Local, ConstrainedInternet, Internet }
public enum AppDeviceIdiom { Unknown, Phone, Tablet, Desktop, TV, Watch, Car }
public enum AppDeviceType { Unknown, Physical, Virtual }
public sealed record Location(double Latitude, double Longitude, double? Altitude, double? Accuracy, DateTimeOffset Timestamp);
public sealed record GeolocationRequest(GeolocationAccuracy Accuracy, TimeSpan Timeout);
public enum GeolocationAccuracy { Lowest, Low, Medium, High, Best }
public sealed class GeolocationChangedEventArgs(Location location) : EventArgs
{
    public Location Location { get; } = location;
}
public sealed record AppAction(string Id, string Title, string? Subtitle, string? Icon);
public sealed class AppActionEventArgs(string actionId) : EventArgs
{
    public string ActionId { get; } = actionId;
}
