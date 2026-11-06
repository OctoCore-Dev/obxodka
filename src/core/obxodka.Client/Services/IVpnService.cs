namespace obxodka.Services;

//интерфейс управления vpn туннелем и событиями состояния
public interface IVpnService
{
    //текущее состояние подключения
    public AppVpnState CurrentState { get; }

    //работает ли туннель
    public bool IsRunning { get; }

    //события смены состояния логов ошибок и трафика
    public event Action<AppVpnState>? OnStateChanged;
    public event Action<string>? OnLogUpdated;
    public event Action<string>? OnErrorOccurred;
    public event Action<AppTrafficStats>? OnTrafficUpdated;
    public event Action<string>? OnForceLogoutRequested;

    //запуск vpn подключения
    public Task StartVpnAsync(string serverIp, int serverPort);

    //запуск vpn подключения с резервными серверами
    public Task StartVpnAsync(string serverIp, int serverPort, IReadOnlyList<VpnServerDto>? fallbackServers) =>
        StartVpnAsync(serverIp, serverPort);

    //остановка vpn подключения
    public Task StopVpnAsync();

    //поиск конфликтующих vpn программ
    public string? DetectConflictingVpn() => null;

    //предварительная проверка сетевой связности
    public Task<string?> RunNetworkPreflightAsync() => Task.FromResult<string?>(null);
}
