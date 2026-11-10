namespace obxodka.Core;

//типизированное исключение отсутствия физического сетевого подключения
public sealed class PhysicalNetworkUnavailableException(string message) : InvalidOperationException(message);

//аппаратный мониторинг и координация физического сетевого линка (uplink guard)
public sealed partial class OctopusEngine
{
    private int _isUplinkListenerRegistered;
    private bool _lastReportedUplinkState = true;

    //событие потери физической сети (wi-fi, кабель или сотовая связь отключены)
    public event Action? OnUplinkLost;

    //событие восстановления физической сети
    public event Action? OnUplinkRestored;

    //проверка наличия физического интернет-соединения без ложных срабатываний кэша
    public static bool IsUplinkAvailable =>
        PlatformServices.Connectivity.NetworkAccess != AppNetworkAccess.None;

    //инициализация кроссплатформенного слушателя физического состояния сети
    public void EnsureUplinkMonitoringInitialized()
    {
        if (Interlocked.CompareExchange(ref _isUplinkListenerRegistered, 1, 0) != 0)
        {
            return;
        }

        _lastReportedUplinkState = IsUplinkAvailable;
        PlatformServices.Connectivity.ConnectivityChanged += HandleConnectivityChanged;
    }

    //обработчик смены сетевого профиля ос
    private void HandleConnectivityChanged(object? sender, AppConnectivityChangedEventArgs e)
    {
        var isOnline = e.NetworkAccess != AppNetworkAccess.None;
        if (_lastReportedUplinkState == isOnline)
        {
            return;
        }

        _lastReportedUplinkState = isOnline;
        if (!isOnline)
        {
            AppLogger.LogWarning("[UPLINK] Physical network lost (Wi-Fi/Ethernet disconnected). Firing OnUplinkLost...");
            OnUplinkLost?.Invoke();
        }
        else
        {
            AppLogger.Log("[UPLINK] Physical network restored. Firing OnUplinkRestored...");
            OnUplinkRestored?.Invoke();
        }
    }

    //быстрая верификация физической доступности сети с прямым зондированием сокета без dns кэша
    public static async Task<bool> VerifyPhysicalUplinkAsync(int timeoutMs = 1500)
    {
        if (!IsUplinkAvailable)
        {
            return false;
        }

        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            //прямой опрос глобального anycast ip cloudflare/google минуя dns кэш
            await socket.ConnectAsync(IPAddress.Parse("1.1.1.1"), 443, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            //запасная попытка через яндекс anycast для изолированных сегментов
            try
            {
                using var cts2 = new CancellationTokenSource(timeoutMs);
                using var socket2 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await socket2.ConnectAsync(IPAddress.Parse("77.88.55.242"), 443, cts2.Token).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
