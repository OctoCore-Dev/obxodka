using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Web;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using MessageBox = System.Windows.MessageBox;

namespace ObxodkaWindows.Core
{
    public class VpnService
    {
        private Process? _vpnProcess;
        private readonly string _engineDir;
        private readonly string _exePath;

        public static class ChildProcessTracker
        {
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

            [DllImport("kernel32.dll")]
            static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInfoClass, IntPtr lpJobObjectInfo, int cbJobObjectInfoLength);

            [DllImport("kernel32.dll")]
            static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

            private static IntPtr s_jobHandle;

            static ChildProcessTracker()
            {
                s_jobHandle = CreateJobObject(IntPtr.Zero, null);
                var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = 0x2000 };
                var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = info };
                int length = Marshal.SizeOf(extendedInfo);
                IntPtr extendedInfoPtr = Marshal.AllocHGlobal(length);
                Marshal.StructureToPtr(extendedInfo, extendedInfoPtr, false);
                SetInformationJobObject(s_jobHandle, 9, extendedInfoPtr, length);
                Marshal.FreeHGlobal(extendedInfoPtr);
            }

            public static void AddProcess(Process process)
            {
                if (process != null && !process.HasExited)
                    AssignProcessToJobObject(s_jobHandle, process.Handle);
            }

            [StructLayout(LayoutKind.Sequential)]
            struct JOBOBJECT_BASIC_LIMIT_INFORMATION { public Int64 PerProcessUserTimeLimit; public Int64 PerJobUserTimeLimit; public int LimitFlags; public UIntPtr MinimumWorkingSetSize; public UIntPtr MaximumWorkingSetSize; public int ActiveProcessLimit; public Int64 Affinity; public int PriorityClass; public int SchedulingClass; }
            [StructLayout(LayoutKind.Sequential)]
            struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION { public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoCounters; public UIntPtr ProcessMemoryLimit; public UIntPtr JobMemoryLimit; public UIntPtr PeakProcessMemoryUsage; public UIntPtr PeakJobMemoryUsage; }
            [StructLayout(LayoutKind.Sequential)]
            struct IO_COUNTERS { public UInt64 ReadOperationCount; public UInt64 WriteOperationCount; public UInt64 OtherOperationCount; public UInt64 ReadTransferCount; public UInt64 WriteTransferCount; public UInt64 OtherTransferCount; }
        }

        public VpnService()
        {
            _engineDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Engine");
            _exePath = Path.Combine(_engineDir, "obxodka-engine.exe");
        }

        public async Task StartVpn(string vlessLink)
        {
            try
            {
                if (!System.IO.File.Exists(_exePath))
                    throw new Exception("Engine not found");

                string jsonConfig = GenerateConfig(vlessLink);
                KillOldProcesses();

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
                    }
                };

                _vpnProcess.Start();

                // ПЕРЕДАЧА КОНФИГА В ОЗУ
                using (StreamWriter sw = _vpnProcess.StandardInput)
                {
                    if (sw.BaseStream.CanWrite)
                    {
                        await sw.WriteAsync(jsonConfig);
                    }
                }

                ChildProcessTracker.AddProcess(_vpnProcess);
                StartWatchdog();

                _vpnProcess.BeginOutputReadLine();
                _vpnProcess.BeginErrorReadLine();

                await Task.Delay(2000);
                SetProxy(true);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error: " + ex.Message);
            }
        }

        private void StartWatchdog()
        {
            try
            {
                string script = $@"
                $parentPid = {Process.GetCurrentProcess().Id};
                while ($true) {{
                    $parent = Get-Process -Id $parentPid -ErrorAction SilentlyContinue;
                    if (!$parent) {{
                        $regPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings';
                        Set-ItemProperty -Path $regPath -Name 'ProxyEnable' -Value 0;
                        Set-ItemProperty -Path $regPath -Name 'ProxyServer' -Value '';
                        exit;
                    }}
                    Start-Sleep -Seconds 1;
                }}";

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -WindowStyle Hidden -Command \"{script}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi);
            }
            catch { }
        }

        public void StopVpn()
        {
            KillOldProcesses();
            SetProxy(false);
        }

        private void KillOldProcesses()
        {
            foreach (var process in Process.GetProcessesByName("obxodka-engine"))
            {
                try { process.Kill(); process.WaitForExit(1000); } catch { }
            }
        }

        public void SetProxy(bool enable)
        {
            try
            {
                using var registry = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
                if (registry != null)
                {
                    registry.SetValue("ProxyEnable", enable ? 1 : 0);
                    registry.SetValue("ProxyServer", enable ? "127.0.0.1:10809" : "");
                    registry.SetValue("ProxyOverride", enable ? "<local>" : "");
                }

                InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
            }
            catch { }
        }

        [DllImport("wininet.dll")]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        private string GenerateConfig(string link)
        {
            var uri = new Uri(link);
            var query = HttpUtility.ParseQueryString(uri.Query);
            int port = uri.Port > 0 ? uri.Port : 8443;

            var config = new
            {
                log = new { level = "info", timestamp = true },
                dns = new { servers = new[] { new { tag = "google", address = "8.8.8.8" } }, final = "google" },
                inbounds = new[] { new { type = "mixed", tag = "mixed-in", listen = "127.0.0.1", listen_port = 10809 } },
                outbounds = new object[] {
                    new {
                        type = "vless",
                        tag = "proxy",
                        server = uri.Host,
                        server_port = port,
                        uuid = uri.UserInfo,
                        tls = new {
                            enabled = true,
                            server_name = query["sni"] ?? "www.nvidia.com",
                            utls = new { enabled = true, fingerprint = query["fp"] ?? "chrome" },
                            reality = new { enabled = true, public_key = query["pbk"], short_id = query["sid"] ?? "" }
                        }
                    },
                    new { type = "direct", tag = "direct" }
                },
                route = new { final = "proxy" }
            };

            return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        }
    }
}