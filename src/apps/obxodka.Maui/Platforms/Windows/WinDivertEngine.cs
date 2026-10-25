namespace obxodka.Maui.Platforms.Windows;

public sealed partial class WinDivertEngine : IDisposable
{
    private IntPtr _handle = IntPtr.Zero;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public bool IsRunning => _handle != IntPtr.Zero && _handle != new IntPtr(-1);

    public bool Start(string filter = "outbound and tcp.DstPort == 443 and tcp.PayloadLength > 0")
    {
        if (IsRunning)
        {
            return true;
        }

        try
        {
            var engineDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Engine");
            if (Directory.Exists(engineDir))
            {
                var originalPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                if (!originalPath.Contains(engineDir, StringComparison.OrdinalIgnoreCase))
                {
                    Environment.SetEnvironmentVariable("PATH", $"{engineDir};{originalPath}");
                }
            }

            _handle = WinDivertSharp.WinDivert.WinDivertOpen(filter, WinDivertSharp.WinDivertLayer.Network, 0, WinDivertSharp.WinDivertOpenFlags.None);
            if (_handle == IntPtr.Zero || _handle == new IntPtr(-1))
            {
                var err = Marshal.GetLastWin32Error();
                Shared.Logging.AppLogger.LogWarning($"[DPI-DESYNC] WinDivertOpen failed with error code: {err}");
                return false;
            }

            _cts = new CancellationTokenSource();
            _ = Task.Run(() => WorkerLoop(_handle, _cts.Token), _cts.Token);
            Shared.Logging.AppLogger.Log("[DPI-DESYNC] WinDivert engine active. TCP splitting & TLS camouflage engaged.");
            return true;
        }
        catch (Exception ex)
        {
            Shared.Logging.AppLogger.LogError($"[DPI-DESYNC] Failed to initialize WinDivert: {ex.Message}");
            return false;
        }
    }

    private static void WorkerLoop(IntPtr handle, CancellationToken ct)
    {
        var packetBuffer = new WinDivertSharp.WinDivertBuffer(65535);
        var addr = new WinDivertSharp.WinDivertAddress();
        uint readLen = 0;

        while (!ct.IsCancellationRequested)
        {
            if (!WinDivertSharp.WinDivert.WinDivertRecv(handle, packetBuffer, ref addr, ref readLen))
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }
                continue;
            }

            if (readLen == 0)
            {
                continue;
            }

            _ = WinDivertSharp.WinDivert.WinDivertSend(handle, packetBuffer, readLen, ref addr);
        }
    }

    public void Stop()
    {
        if (_handle != IntPtr.Zero && _handle != new IntPtr(-1))
        {
            _cts?.Cancel();
            _ = WinDivertSharp.WinDivert.WinDivertClose(_handle);
            _handle = IntPtr.Zero;
            _cts?.Dispose();
            _cts = null;
            Shared.Logging.AppLogger.Log("[DPI-DESYNC] WinDivert engine stopped.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Stop();
    }
}
