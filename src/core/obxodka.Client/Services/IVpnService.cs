namespace obxodka.Services;

//интерфейс управления vpn туннелем и событиями состояния
public interface IVpnService
{
    //текущее состояние подключения
    AppVpnState CurrentState { get; }

    //работает ли туннель
    bool IsRunning { get; }

    //события смены состояния логов ошибок и трафика
    event Action<AppVpnState>? OnStateChanged;
    event Action<string>? OnLogUpdated;
    event Action<string>? OnErrorOccurred;
    event Action<AppTrafficStats>? OnTrafficUpdated;
    event Action<string>? OnForceLogoutRequested;

    //запуск vpn подключения
    Task StartVpnAsync(string serverIp, int serverPort);

    //запуск vpn подключения с резервными серверами
    Task StartVpnAsync(string serverIp, int serverPort, IReadOnlyList<VpnServerDto>? fallbackServers) =>
        StartVpnAsync(serverIp, serverPort);

    //остановка vpn подключения
    Task StopVpnAsync();

    //поиск конфликтующих vpn программ
    string? DetectConflictingVpn() => null;

    //предварительная проверка сетевой связности
    Task<string?> RunNetworkPreflightAsync() => Task.FromResult<string?>(null);
}
