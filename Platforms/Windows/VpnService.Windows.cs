namespace obxodka.Platforms.Windows;
internal sealed class WindowsVpnService : IVpnService, IDisposable
{
    private Process? _vpnProcess;
    private bool _isManualStop;
    private readonly string _engineDir = Path.Combine(AppContext.BaseDirectory, "Engine");
    private readonly string _exePath = Path.Combine(AppContext.BaseDirectory, "Engine", "obxodka-engine.exe");
    private readonly SemaphoreSlim _vpnLock = new(1, 1);
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    public AppVpnState CurrentState { get; private set; } = AppVpnState.Disconnected;
    public bool IsRunning => CurrentState == AppVpnState.Connected;
    public event Action<AppVpnState>? OnStateChanged;
    public event Action<string>? OnErrorOccurred;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051")]
    public event Action<AppTrafficStats>? OnTrafficUpdated;
    [DllImport("wininet.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
    internal static class ChildProcessTracker
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);
        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInfoClass, IntPtr lpJobObjectInfo, int cbJobObjectInfoLength);
        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
        private static readonly IntPtr s_jobHandle = InitializeJobObject();
        private static IntPtr InitializeJobObject()
        {
            IntPtr handle = CreateJobObject(IntPtr.Zero, null);
            var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = 0x2000 };
            var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = info };
            int length = Marshal.SizeOf(extendedInfo);
            IntPtr extendedInfoPtr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(extendedInfo, extendedInfoPtr, false);
                SetInformationJobObject(handle, 9, extendedInfoPtr, length);
            }
            finally
            {
                Marshal.FreeHGlobal(extendedInfoPtr);
            }
            return handle;
        }
        public static void AddProcess(Process process)
        {
            if (process != null && !process.HasExited)
                AssignProcessToJobObject(s_jobHandle, process.Handle);
        }
        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION { public long PerProcessUserTimeLimit; public long PerJobUserTimeLimit; public int LimitFlags; public nuint MinimumWorkingSetSize; public nuint MaximumWorkingSetSize; public int ActiveProcessLimit; public long Affinity; public int PriorityClass; public int SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION { public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoCounters; public nuint ProcessMemoryLimit; public nuint JobMemoryLimit; public nuint PeakProcessMemoryUsage; public nuint PeakJobMemoryUsage; }
        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS { public ulong ReadOperationCount; public ulong WriteOperationCount; public ulong OtherOperationCount; public ulong ReadTransferCount; public ulong WriteTransferCount; public ulong OtherTransferCount; }
    }
    private void ChangeState(AppVpnState newState)
    {
        if (CurrentState == newState) return;
        CurrentState = newState;
        MainThread.BeginInvokeOnMainThread(() => OnStateChanged?.Invoke(CurrentState));
    }
    public async Task StartVpn(string vlessLink, bool useAdblock = false)
    {
        await _vpnLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _isManualStop = false;
            ChangeState(AppVpnState.Connecting);
            string jsonConfig = GenerateWindowsConfig(vlessLink ?? string.Empty, useAdblock);
            if (!File.Exists(_exePath))
                throw new InvalidOperationException($"Движок не найден по пути: {_exePath}");
            await KillOldProcessesAsync().ConfigureAwait(false);
            SetProxy(false);
            FlushDns();
            _vpnProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _exePath,
                    Arguments = "run -c stdin",
                    WorkingDirectory = _engineDir,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };
            _vpnProcess.OutputDataReceived += (s, e) => { if (e.Data != null) Debug.WriteLine($"[ENGINE OUT]: {e.Data}"); };
            _vpnProcess.ErrorDataReceived += (s, e) => { if (e.Data != null) Debug.WriteLine($"[ENGINE ERR]: {e.Data}"); };
            _vpnProcess.Exited += (sender, args) =>
            {
                if (!_isManualStop && (CurrentState == AppVpnState.Connected || CurrentState == AppVpnState.Connecting))
                {
                    ChangeState(AppVpnState.Error);
                    OnErrorOccurred?.Invoke("Движок неожиданно завершил работу.");
                    StopVpn();
                }
            };
            _vpnProcess.Start();
            ChildProcessTracker.AddProcess(_vpnProcess);
            _vpnProcess.BeginOutputReadLine();
            _vpnProcess.BeginErrorReadLine();
            using (StreamWriter sw = _vpnProcess.StandardInput)
            {
                if (sw.BaseStream.CanWrite)
                {
                    await sw.WriteAsync(jsonConfig).ConfigureAwait(false);
                    await sw.FlushAsync().ConfigureAwait(false);
                }
            }
            await Task.Delay(2000).ConfigureAwait(false);
            var currentProcess = _vpnProcess;
            if (currentProcess == null || currentProcess.HasExited)
            {
                if (!_isManualStop)
                    throw new InvalidOperationException("Движок завершился с ошибкой при запуске. Возможно, порт 10809 занят или нужны права администратора.");
                return;
            }
            SetProxy(true);
            ChangeState(AppVpnState.Connected);
        }
        catch (Exception ex)
        {
            if (!_isManualStop)
            {
                ChangeState(AppVpnState.Error);
                OnErrorOccurred?.Invoke(ex.Message);
                StopVpn();
            }
        }
        finally
        {
            _vpnLock.Release();
        }
    }
    public void StopVpn()
    {
        _isManualStop = true;
        SetProxy(false);
        FlushDns();
        try
        {
            foreach (var p in Process.GetProcessesByName("obxodka-engine"))
            {
                p.Kill(true);
                p.Dispose();
            }
        }
        catch { }
        _vpnProcess?.Dispose();
        _vpnProcess = null;
        ChangeState(AppVpnState.Disconnected);
    }
    ~WindowsVpnService()
    {
        SetProxy(false);
    }
    private static async Task KillOldProcessesAsync()
    {
        foreach (var process in Process.GetProcessesByName("obxodka-engine"))
        {
            try
            {
                process.Kill(true);
                using var cts = new CancellationTokenSource(1000);
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch { }
            finally { process.Dispose(); }
        }
    }
    private static void SetProxy(bool enable)
    {
        try
        {
            using var registry = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
            if (registry != null)
            {
                registry.SetValue("ProxyEnable", enable ? 1 : 0);
                registry.SetValue("ProxyServer", enable ? "127.0.0.1:10809" : "");
                registry.SetValue("ProxyOverride", enable ? "<local>;127.0.0.1;localhost" : "");
            }
            InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
        }
        catch { }
    }
    private static void FlushDns()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "ipconfig",
                Arguments = "/flushdns",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            p?.WaitForExit(500);
            Debug.WriteLine("[SYSTEM] DNS Cache Flushed.");
        }
        catch { }
    }
    private static string GenerateWindowsConfig(string link, bool useAdblock)
    {
        var uri = new Uri(link);
        var query = HttpUtility.ParseQueryString(uri.Query);
        int port = uri.Port > 0 ? uri.Port : 443;
        string serverIp = uri.Host;
        string appName = Process.GetCurrentProcess().ProcessName + ".exe";
        string selectedDns = useAdblock ? "94.140.14.15" : "8.8.8.8";
        bool isIp = System.Net.IPAddress.TryParse(serverIp, out _);
        object serverRouteRule = isIp
            ? new { ip_cidr = new[] { $"{serverIp}/32" }, outbound = "direct" }
            : new { domain = new[] { serverIp }, outbound = "direct" };
        var config = new
        {
            log = new { level = "info", timestamp = true },
            dns = new
            {
                servers = new object[] {
                    new {
                        tag = "main-dns",
                        address = selectedDns,
                        address_resolver = "local-dns",
                        detour = "direct"
                    },
                    new {
                        tag = "local-dns",
                        address = "local",
                        detour = "direct"
                    }
                },
                rules = new object[] {
                    new { outbound = "any", server = "main-dns" }
                },
                final = "main-dns"
            },
            inbounds = new object[]
            {
                new { type = "mixed", tag = "mixed-in", listen = "127.0.0.1", listen_port = 10809 },
                new {
                    type = "tun",
                    tag = "tun-in",
                    interface_name = "obxodka-tun",
                    address = new[] { "172.19.0.1/30" },
                    auto_route = true,
                    strict_route = false,
                    stack = "system",
                    sniff = false,
                    endpoint_independent_nat = true
                }
            },
            outbounds = new object[]
            {
                new {
                    type = "vless",
                    tag = "proxy",
                    server = serverIp,
                    server_port = port,
                    uuid = uri.UserInfo,
                    domain_strategy = "prefer_ipv4",
                    tls = new {
                        enabled = true,
                        server_name = query["sni"] ?? serverIp,
                        utls = new { enabled = true, fingerprint = query["fp"] ?? "chrome" },
                        reality = new { enabled = true, public_key = query["pbk"], short_id = query["sid"] ?? "" }
                    }
                },
                new { type = "direct", tag = "direct" }
            },
            route = new
            {
                rules = new object[]
                {
                    new { protocol = "dns", outbound = "direct" },
                    new { process_name = new[] { appName, "obxodka-engine.exe", "powershell.exe" }, outbound = "direct" },
                    serverRouteRule
                },
                final = "proxy",
                auto_detect_interface = true
            }
        };
        return JsonSerializer.Serialize(config, _jsonOptions);
    }
    public void Dispose()
    {
        _vpnLock.Dispose();
        _vpnProcess?.Dispose();
    }
}