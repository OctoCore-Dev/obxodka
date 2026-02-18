using Microsoft.Win32;
using ObxodkaWindows.Core;
using ObxodkaWindows.Models;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace ObxodkaWindows
{
    public partial class App : Application
    {
        private NotifyIcon? _notifyIcon;
        private static Mutex? _mutex;

        private static readonly HttpClient _httpClient = new HttpClient
        {
            BaseAddress = new Uri("YOUR_SERVER_API_URL"),
            Timeout = TimeSpan.FromSeconds(15)
        };

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        extern static bool DestroyIcon(IntPtr handle);

        private const int SW_RESTORE = 9;

        public async Task CheckForUpdatesAsync(MainWindow win)
        {
            try
            {
                var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
                if (currentVersion == null) return;

                var update = await _httpClient.GetFromJsonAsync<UpdateInfo>("/api/App/latest-version");

                if (update != null && !string.IsNullOrEmpty(update.Version))
                {
                    var latestVersion = new Version(update.Version);

                    if (latestVersion > currentVersion)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            win.MainFrame.Navigate(new UpdatePage(update));
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Update Error]: {ex.Message}");
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            if (e.Args.Contains("--uninstall"))
            {
                DoSilentUninstall();
                return;
            }

            _mutex = new Mutex(true, "Obxodka_Unique_System_Mutex_Key", out bool createdNew);

            if (!createdNew)
            {
                ActivateExistingInstance();
                Application.Current.Shutdown();
                return;
            }

            new VpnService().SetProxy(false);
            SetAutostart(true);
            InitTray();

            base.OnStartup(e);

            SplashWindow splash = new SplashWindow();
            splash.Show();
        }

        public void LaunchMainWindow()
        {
            MainWindow window = new MainWindow();
            window.Width = 380;
            window.Height = 520;
            window.ResizeMode = ResizeMode.NoResize;
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            this.MainWindow = window;

            var session = AuthManager.LoadSession();
            if (session.IsLoggedIn)
            {
                window.MainFrame.Navigate(new MainPage());
            }
            else
            {
                window.MainFrame.Navigate(new LoginPage());
            }

            window.Show();

            _ = CheckForUpdatesAsync(window);
        }

        private void DoSilentUninstall()
        {
            try
            {
                string installDir = AppDomain.CurrentDomain.BaseDirectory;
                string regPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(regPath, true))
                {
                    key?.DeleteSubKeyTree("Obxodka", false);
                }

                string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Obxodka.lnk");
                if (System.IO.File.Exists(desktopLnk)) System.IO.File.Delete(desktopLnk);

                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c timeout /t 1 & rd /s /q \"{installDir.TrimEnd('\\')}\"",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                });
            }
            catch { }
            finally
            {
                Application.Current.Shutdown();
            }
        }

        private void ActivateExistingInstance()
        {
            Process current = Process.GetCurrentProcess();
            foreach (Process process in Process.GetProcessesByName(current.ProcessName))
            {
                if (process.Id != current.Id)
                {
                    IntPtr handle = process.MainWindowHandle;
                    if (handle != IntPtr.Zero)
                    {
                        ShowWindow(handle, SW_RESTORE);
                        SetForegroundWindow(handle);
                    }
                    break;
                }
            }
        }

        private void InitTray()
        {
            _notifyIcon = new NotifyIcon();
            _notifyIcon.Text = "obxodka secure network";

            try
            {
                var uri = new Uri("pack://application:,,,/Resources/appicon.png");
                var streamInfo = Application.GetResourceStream(uri);
                if (streamInfo != null)
                {
                    using (var stream = streamInfo.Stream)
                    {
                        var bitmap = new Bitmap(stream);
                        IntPtr hIcon = bitmap.GetHicon();
                        _notifyIcon.Icon = Icon.FromHandle(hIcon);
                    }
                }
            }
            catch
            {
                _notifyIcon.Icon = SystemIcons.Shield;
            }

            _notifyIcon.Visible = true;

            var contextMenu = new ContextMenuStrip();
            var openItem = new ToolStripMenuItem("Открыть obxodka", null, (s, e) => ShowMainWindow());
            var exitItem = new ToolStripMenuItem("Выход", null, (s, e) => Application.Current.Shutdown());

            contextMenu.Items.Add(openItem);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(exitItem);

            _notifyIcon.ContextMenuStrip = contextMenu;

            _notifyIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) ShowMainWindow();
            };
        }

        public void ShowMainWindow()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (Application.Current.MainWindow is MainWindow win)
                {
                    win.Show();
                    if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;

                    var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250));
                    win.BeginAnimation(Window.OpacityProperty, fadeIn);

                    win.Activate();
                    win.Focus();
                }
            });
        }

        private void SetAutostart(bool enable)
        {
            try
            {
                string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
                using var key = Registry.CurrentUser.OpenSubKey(path, true);
                if (key != null)
                {
                    if (enable)
                    {
                        string appPath = $"\"{Process.GetCurrentProcess().MainModule?.FileName}\" --minimized";
                        key.SetValue("Obxodka", appPath);
                    }
                    else
                    {
                        key.DeleteValue("Obxodka", false);
                    }
                }
            }
            catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            new VpnService().SetProxy(false);

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                if (_notifyIcon.Icon != null)
                {
                    DestroyIcon(_notifyIcon.Icon.Handle);
                }
                _notifyIcon.Dispose();
            }

            if (_mutex != null)
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
            }

            KillEngineProcess("obxodka-engine");
            base.OnExit(e);
        }

        private void KillEngineProcess(string processName)
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    process.Kill();
                    process.WaitForExit(1000);
                }
            }
            catch { }
        }
    }
}