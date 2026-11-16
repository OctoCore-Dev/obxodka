using obxodka.Core.Models;

namespace obxodka.Platforms.Windows;

//платформенный драйвер windows (wintun, маршруты и защита от утечек) для единого оркестратора vpn
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed partial class WindowsVpnService : ITunnelPlatformAdapter, IDisposable
{
    private WintunAdapter? _adapter;
    private Task? _trafficTask;
    private Action? _platformNetworkChanged;
    private long _lastNetChangeTicks;
    private static int s_suppressNetworkChangeDepth;
    private static long s_suppressNetworkChangeUntilTicks;

    public static SplitTunnelPolicy SplitTunnelPolicy => WindowsSplitTunnelEngine.Policy;

    public bool SupportsKillSwitch => true;

    public event Action? OnPlatformNetworkChanged
    {
        add => _platformNetworkChanged += value;
        remove => _platformNetworkChanged -= value;
    }

    public WindowsVpnService()
    {
        AppManager.SyncPolicyFromPreferences();
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnSystemNetworkAddressChanged;
        _ = Task.Run(CleanupStaleRoutesAsync);
    }

    //временное подавление событий смены сети на время собственной настройки wintun и dns
    private static NetworkChangeSuppressionScope SuppressNetworkChanges(int graceMs = 2500) =>
        new(graceMs);

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

    //запуск системной утилиты с таймаутом
    private static async Task<(int exitCode, string output)> RunCmdAsync(
        string fileName,
        string args,
        int timeoutMs = 4000,
        CancellationToken ct = default)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(fileName, args)
                {
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                }
            };

            if (!proc.Start())
            {
                return (-1, "Failed to start process");
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(timeoutMs);

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(linkedCts.Token);

            try
            {
                await proc.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch { }

                return (-1, "Command timed out or cancelled");
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return (proc.ExitCode, string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    //освобождение ресурсов windows-драйвера
    public void Dispose()
    {
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnSystemNetworkAddressChanged;
        _adapter?.Dispose();
        _adapter = null;
    }
}
