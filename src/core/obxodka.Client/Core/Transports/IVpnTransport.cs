namespace obxodka.Core.Transports;

//базовый контракт для vpn транспорта туннеля
public interface IVpnTransport : IDisposable, IAsyncDisposable
{
    //имя активного протокола
    string ProtocolName { get; }

    //флаг активности соединения
    bool IsConnected { get; }

    //событие приема входящего сетевого пакета
    event Action<byte[], int>? OnPacketReceived;

    //событие обновления сетевого пинга
    event Action<long>? OnPingUpdated;

    //событие обрыва соединения
    event Action? OnConnectionDropped;

    //событие согласования или изменения mtu
    event Action<int>? OnMtuUpdated;

    //подключение к серверу и получение назначенного ip адреса
    Task<(string ip, string ip6)> ConnectAsync(string serverIp, string thumbprint, CancellationToken ct);

    //отправка исходящего пакета из пула памяти
    void SendPacketFromPool(byte[] packet, int length);

    //отправка сигнала корректного отключения
    Task SendDisconnectSignalAsync();

    //отправка системного эхо зонда для замера пинга
    Task SendPingProbeAsync();

    //проверка проходимости mtu заданного размера
    Task<bool> ProbeMtuAsync(int size, CancellationToken ct) => Task.FromResult(true);

    //пакетный замер максимального рабочего mtu
    Task<int[]> MeasureBurstMtuAsync(CancellationToken ct) => Task.FromResult<int[]>([]);

    //синхронизация значения mtu с сервером
    Task SendMtuSyncAsync(int mtu) => Task.CompletedTask;

    //защита сокетов от зацикливания vpn маршрута
    void ProtectSockets(Action<Socket> protectAction);
}
