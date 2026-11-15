using obxodka.Core.Models;

namespace obxodka.Platforms.Windows;

//платформенный драйвер windows (wintun, маршруты и защита от утечек) для единого оркестратора vpn
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed partial class WindowsVpnService : ITunnelPlatformAdapter, IDisposable
{
    private WintunAdapter? _adapter;
    private Task? _trafficTask;

    public static SplitTunnelPolicy SplitTunnelPolicy => WindowsSplitTunnelEngine.Policy;

    public bool SupportsKillSwitch => true;

    public event Action? OnPlatformNetworkChanged
    {
        add { }
        remove { }
    }

    public WindowsVpnService()
    {
        AppManager.SyncPolicyFromPreferences();
        _ = Task.Run(CleanupStaleRoutesAsync);
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
        _adapter?.Dispose();
        _adapter = null;
    }
}
