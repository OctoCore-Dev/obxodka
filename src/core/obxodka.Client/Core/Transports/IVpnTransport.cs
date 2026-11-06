namespace obxodka.Core.Transports;

//базовый контракт для vpn транспорта туннеля
public interface IVpnTransport : IDisposable, IAsyncDisposable
{
    //имя активного протокола
    public string ProtocolName { get; }

    //флаг активности соединения
    public bool IsConnected { get; }

    //событие приема входящего сетевого пакета
    public event Action<byte[], int>? OnPacketReceived;

    //событие обновления сетевого пинга
    public event Action<long>? OnPingUpdated;

    //событие обрыва соединения
    public event Action? OnConnectionDropped;

    //событие согласования или изменения mtu
    public event Action<int>? OnMtuUpdated;

    //подключение к серверу и получение назначенного ip адреса
    public Task<(string ip, string ip6)> ConnectAsync(string serverIp, string thumbprint, CancellationToken ct);

    //отправка исходящего пакета из пула памяти
    public void SendPacketFromPool(byte[] packet, int length);

    //отправка сигнала корректного отключения
    public Task SendDisconnectSignalAsync();

    //отправка системного эхо зонда для замера пинга
    public Task SendPingProbeAsync();

    //проверка проходимости mtu заданного размера
    public Task<bool> ProbeMtuAsync(int size, CancellationToken ct) => Task.FromResult(true);

    //пакетный замер максимального рабочего mtu
    public Task<int[]> MeasureBurstMtuAsync(CancellationToken ct) => Task.FromResult<int[]>([]);

    //синхронизация значения mtu с сервером
    public Task SendMtuSyncAsync(int mtu) => Task.CompletedTask;

    //защита сокетов от зацикливания vpn маршрута
    public void ProtectSockets(Action<Socket> protectAction);
}
