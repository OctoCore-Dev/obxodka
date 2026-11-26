namespace obxodka.Platforms.Windows;

//платформенный драйвер windows (wintun, маршруты и защита от утечек) для единого оркестратора vpn
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed partial class WindowsVpnService : ITunnelPlatformAdapter, IDisposable
{
    private static int s_suppressNetworkChangeDepth;
    private static long s_suppressNetworkChangeUntilTicks;
    private WintunAdapter? _adapter;
    private long _lastNetChangeTicks;
    private Action? _platformNetworkChanged;
    private Task? _trafficTask;

    //обратный вызов хоста (maui/cli/avalonia) для синхронизации политики сплит-туннеля из настроек
    public static Action? SyncSplitTunnelPolicy { get; set; }

    public WindowsVpnService()
    {
        SyncSplitTunnelPolicy?.Invoke();
        NetworkChange.NetworkAddressChanged += OnSystemNetworkAddressChanged;
        _ = Task.Run(CleanupStaleRoutesAsync);
    }

    public static SplitTunnelPolicy SplitTunnelPolicy => WindowsSplitTunnelEngine.Policy;

    //освобождение ресурсов windows-драйвера
    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnSystemNetworkAddressChanged;
        _adapter?.Dispose();
        _adapter = null;
    }

    public bool SupportsKillSwitch => true;

    public event Action? OnPlatformNetworkChanged
    {
        add => _platformNetworkChanged += value;
        remove => _platformNetworkChanged -= value;
    }

    //временное подавление событий смены сети на время собственной настройки wintun и DNS
    private static NetworkChangeSuppressionScope SuppressNetworkChanges(int graceMs = 2500) =>
        new(graceMs);

    private static bool IsNetworkChangeSuppressed() =>
        Volatile.Read(ref s_suppressNetworkChangeDepth) > 0 ||
        Environment.TickCount64 < Volatile.Read(ref s_suppressNetworkChangeUntilTicks);

    //уведомление оркестратора о смене системного сетевого адреса с защитой от дребезга и ложных срабатываний
    private void OnSystemNetworkAddressChanged(object? sender, EventArgs e)
    {
        if (IsNetworkChangeSuppressed() || _platformNetworkChanged is null)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastNetChangeTicks) < 500)
        {
            return;
        }

        _ = Interlocked.Exchange(ref _lastNetChangeTicks, now);
        _ = Task.Run(async () =>
        {
            await Task.Delay(150).ConfigureAwait(false);
            if (IsNetworkChangeSuppressed() || !HasPhysicalNetworkSnapshotChanged())
            {
                return;
            }

            ResetSavedPhysicalGateway();
            _platformNetworkChanged?.Invoke();
        });
    }

    private sealed partial class NetworkChangeSuppressionScope : IDisposable
    {
        private readonly int _graceMs;
        private int _disposed;

        public NetworkChangeSuppressionScope(int graceMs)
        {
            _graceMs = graceMs;
            _ = Interlocked.Increment(ref s_suppressNetworkChangeDepth);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _ = Interlocked.Exchange(ref s_suppressNetworkChangeUntilTicks, Environment.TickCount64 + _graceMs);
            _ = Interlocked.Decrement(ref s_suppressNetworkChangeDepth);
        }
    }
}
