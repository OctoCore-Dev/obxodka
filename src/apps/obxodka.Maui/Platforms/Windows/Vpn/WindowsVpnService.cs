using obxodka.Core.Models;

namespace obxodka.Platforms.Windows;

//управление жизненным циклом и состоянием vpn сервиса windows
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed partial class WindowsVpnService : IVpnService, IDisposable
{
    private CancellationTokenSource? _cts;
    private WintunAdapter? _adapter;
    private Task? _trafficTask;

    public AppVpnState CurrentState { get; private set; } = AppVpnState.Disconnected;
    public bool IsRunning => CurrentState == AppVpnState.Connected;

    public event Action<AppVpnState>? OnStateChanged;
    public event Action<string>? OnLogUpdated;
    public event Action<string>? OnErrorOccurred;
    public event Action<AppTrafficStats>? OnTrafficUpdated = delegate { };
    public event Action<string>? OnForceLogoutRequested;

    private string _currentServerIp = "";
    private readonly List<string> _currentServerIpsToRoute = [];
    private bool _isExplicitlyStopped;
    private List<VpnServerDto> _fallbackServers = [];
    private int _currentServerIndex;
    private int _isHandlingDeadConnection;
    private int _isHandlingDrop;
    private string _currentAssignedIp = "";
    private readonly SemaphoreSlim _vpnGate = new(1, 1);
    public static SplitTunnelPolicy SplitTunnelPolicy => WindowsSplitTunnelEngine.Policy;

    public WindowsVpnService()
    {
        AppManager.SyncPolicyFromPreferences();
        OctopusEngine.Current.ProtectTransportSockets(BindSocketToPhysicalAdapter);
        ObxodkaStreamTransport.OnSocketCreated = BindSocketToPhysicalAdapter;
        ObxodkaWebSocketTransport.OnSocketCreated = BindSocketToPhysicalAdapter;
        OctopusEngine.Current.OnStatusMessage += msg => OnLogUpdated?.Invoke(msg);
        _ = Task.Run(CleanupStaleRoutesAsync);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                _ = StopVpnAsync().Wait(TimeSpan.FromSeconds(2));
            }
            catch { }
        };
        OctopusEngine.OnCertificateRevoked += msg => OnForceLogoutRequested?.Invoke(msg);
        OctopusEngine.Current.OnMtuUpdated -= HandleMtuUpdated;
        OctopusEngine.Current.OnMtuUpdated += HandleMtuUpdated;
        OctopusEngine.Current.OnConnectionDropped -= HandleEngineDrop;
        OctopusEngine.Current.OnConnectionDropped += HandleEngineDrop;
        OctopusEngine.Current.OnDeadConnectionDetected -= HandleDeadConnection;
        OctopusEngine.Current.OnDeadConnectionDetected += HandleDeadConnection;
    }

    //обновление внутреннего состояния vpn сервиса
    private void UpdateState(AppVpnState state)
    {
        CurrentState = state;
        OnStateChanged?.Invoke(state);
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

    //освобождение ресурсов vpn сервиса
    public void Dispose()
    {
        OctopusEngine.Current.OnConnectionDropped -= HandleEngineDrop;
        OctopusEngine.Current.OnDeadConnectionDetected -= HandleDeadConnection;
        _ = StopVpnAsync();
        _cts?.Dispose();
        _vpnGate.Dispose();
        _adapter?.Dispose();
        _adapter = null;
    }
}
