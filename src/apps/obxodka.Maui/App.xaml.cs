#if WINDOWS
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;
using Colors = Microsoft.UI.Colors;
using Grid = Microsoft.UI.Xaml.Controls.Grid;
using Window = Microsoft.Maui.Controls.Window;
#endif

namespace obxodka;

//тип отложенного действия при переходе из фоновой плитки или голосового ассистента в ui
internal enum BackgroundVpnActionKind
{
    None = 0,
    NoInternet = 1,
    NoBalance = 2,
    RequireAuth = 3,
    RequireVpnPermission = 4,
    AssistantDisabled = 5
}

internal sealed partial class App
{
    private static int s_shutdownInitiated;

    public App()
    {
        InitializeComponent();
#if WINDOWS
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            //аварийное восстановление родного часового пояса пользователя если приложение ранее закрылось некорректно
            WindowsTimeZoneService.RestoreOriginalIfNeeded("запуск приложения после предыдущей сессии");
        }
#endif
        DnsAdBlocker.IsAdBlockEnabled = Preferences.Default.Get(AppPreferenceKeys.UseAdblockDns, true);
        _ = Task.Run(async () => await TelemetryService.Instance.FlushPreviousSessionCrashLogAsync().ConfigureAwait(false));
        _ = Task.Run(async () =>
        {
            try
            {
                var cacheFile = Path.Combine(FileSystem.AppDataDirectory, "adblock_rules.txt");
                _ = await DnsAdBlockRuleManager.LoadFromCacheAsync(cacheFile);

                var lastSyncTicks = Preferences.Default.Get(AppPreferenceKeys.AdblockLastSyncTicks, 0L);
                var lastSync = new DateTime(lastSyncTicks, DateTimeKind.Utc);
                if (DateTime.UtcNow - lastSync > TimeSpan.FromHours(24))
                {
                    if (await DnsAdBlockRuleManager.SyncFromRemoteAsync(cacheFilePath: cacheFile))
                    {
                        Preferences.Default.Set(AppPreferenceKeys.AdblockLastSyncTicks, DateTime.UtcNow.Ticks);
                    }
                }
            }
            catch
            {
                // ignored
            }
        });
    }

    public static (string Email, string Code)? PendingDeepLinkLogin { get; set; }
    public static bool PendingTileAction { get; set; }
    public static BackgroundVpnActionKind PendingBackgroundAction { get; set; }
    public static bool IsShutdownCompleted { get; private set; }

#if WINDOWS
    public static IntPtr MainWindowHandle { get; set; } = IntPtr.Zero;
#endif
    public static event Action? AppResumed;
    public static event Action? WindowActivated;
    public static event Action? WindowDeactivated;
    public static event Action<bool>? WindowVisibilityChanged;
    public static event Action<string, string>? DeepLinkLoginReceived;
    public static event Action<BackgroundVpnActionKind>? BackgroundActionReceived;

    //регистрация фонового действия из плитки или голосового помощника для показа диалога в ui
    public static void HandleBackgroundAction(BackgroundVpnActionKind action)
    {
        if (action == BackgroundVpnActionKind.None)
        {
            return;
        }

        PendingBackgroundAction = action;
        MainThread.BeginInvokeOnMainThread(() => BackgroundActionReceived?.Invoke(action));
    }

    public static bool TryParseDeepLinkLogin(string? rawUri, out string email, out string code)
    {
        email = string.Empty;
        code = string.Empty;
        if (string.IsNullOrWhiteSpace(rawUri))
        {
            return false;
        }

        var trimmed = rawUri.Trim().Trim('"');
        var idx = trimmed.IndexOf("obxodka://", StringComparison.OrdinalIgnoreCase);
        if (idx > 0)
        {
            trimmed = trimmed[idx..];
        }
        else
        {
            var httpsIdx = trimmed.IndexOf($"https://{AppConfig.PrimaryDomain}/qr", StringComparison.OrdinalIgnoreCase);
            if (httpsIdx > 0)
            {
                trimmed = trimmed[httpsIdx..];
            }
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var isCustomScheme = string.Equals(uri.Scheme, "obxodka", StringComparison.OrdinalIgnoreCase);
        var isWebQrLink = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
                          (IsTrustedDeepLinkHost(uri.Host, AppConfig.PrimaryDomain) ||
                           IsTrustedDeepLinkHost(uri.Host, AppConfig.MirrorDomain)) &&
                          uri.AbsolutePath.StartsWith("/qr", StringComparison.OrdinalIgnoreCase);

        if (!isCustomScheme && !isWebQrLink)
        {
            return false;
        }

        var query = uri.Query;
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        var pairs = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var kv = pair.Split('=', 2);
            if (kv.Length != 2)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(kv[0]).Trim();
            var val = Uri.UnescapeDataString(kv[1]).Trim();
            if (string.Equals(key, "email", StringComparison.OrdinalIgnoreCase))
            {
                email = val;
            }
            else if (string.Equals(key, "code", StringComparison.OrdinalIgnoreCase))
            {
                code = val;
            }
        }

        return !string.IsNullOrWhiteSpace(email) &&
               email.Contains('@') &&
               code.Length == 6 &&
               code.All(char.IsAsciiDigit);
    }

    //проверка принадлежности хоста доверенному домену или его поддомену
    private static bool IsTrustedDeepLinkHost(string? host, string? trustedDomain) =>
        !string.IsNullOrWhiteSpace(host) &&
        !string.IsNullOrWhiteSpace(trustedDomain) &&
        (string.Equals(host, trustedDomain, StringComparison.OrdinalIgnoreCase) ||
         host.EndsWith($".{trustedDomain}", StringComparison.OrdinalIgnoreCase));

    public static void HandleDeepLink(string? rawUri)
    {
        if (!TryParseDeepLinkLogin(rawUri, out var email, out var code))
        {
            return;
        }

        PendingDeepLinkLogin = (email, code);
        MainThread.BeginInvokeOnMainThread(() => DeepLinkLoginReceived?.Invoke(email, code));
    }

    public static void InitiateGracefulBackgroundShutdown()
    {
        if (Interlocked.Exchange(ref s_shutdownInitiated, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var vpnService = IPlatformApplication.Current?.Services.GetService<IVpnService>();
                var stopTask = vpnService?.StopVpnAsync() ?? Task.CompletedTask;
                await stopTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
            finally
            {
                IsShutdownCompleted = true;
                Environment.Exit(0);
            }
        });
    }

    public static void HandleTileClick()
    {
        PendingTileAction = true;
        MainThread.BeginInvokeOnMainThread(async void () =>
        {
            try
            {
                if (Shell.Current is null)
                {
                    return;
                }

                await Shell.Current.GoToAsync("//main");
                if (Shell.Current.CurrentPage is MainPage mp)
                {
                    mp.HandlePendingTileAction();
                }
            }
            catch
            {
                // ignored
            }
        });
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new AppShell())
        {
            TitleBar = null
        };
        window.Resumed += (_, _) =>
        {
            WindowVisibilityChanged?.Invoke(true);
            AppResumed?.Invoke();
        };
        window.Activated += (_, _) => WindowActivated?.Invoke();
        window.Deactivated += (_, _) => WindowDeactivated?.Invoke();
        window.Stopped += (_, _) =>
        {
            WindowVisibilityChanged?.Invoke(false);
            WindowDeactivated?.Invoke();
        };

#if WINDOWS
        window.HandlerChanged += (_, _) =>
        {
            if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window winUiWindow)
            {
                return;
            }

            winUiWindow.Activated += (_, args) =>
            {
                if (args.WindowActivationState == WindowActivationState.Deactivated)
                {
                    WindowDeactivated?.Invoke();
                }
                else
                {
                    WindowActivated?.Invoke();
                }
            };
            winUiWindow.VisibilityChanged += (_, args) =>
            {
                WindowVisibilityChanged?.Invoke(args.Visible);
                if (!args.Visible)
                {
                    WindowDeactivated?.Invoke();
                }
                else
                {
                    WindowActivated?.Invoke();
                }
            };

            winUiWindow.ExtendsContentIntoTitleBar = true;
            winUiWindow.SetTitleBar(new Grid
            {
                Height = 0,
                MaxHeight = 0
            });

            var handle = WindowNative.GetWindowHandle(winUiWindow);
            MainWindowHandle = handle;
            var id = Win32Interop.GetWindowIdFromWindow(handle);
            var appWindow = AppWindow.GetFromWindowId(id);
            if (appWindow is not null && AppWindowTitleBar.IsCustomizationSupported())
            {
                var titleBar = appWindow.TitleBar;
                titleBar.ExtendsContentIntoTitleBar = true;
                titleBar.BackgroundColor = Colors.Transparent;
                titleBar.InactiveBackgroundColor = Colors.Transparent;
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

                void SyncTitleBarColors()
                {
                    var bgBase = Current?.Resources != null && Current.Resources.TryGetValue("BgBase", out var bObj) && bObj is Color c
                        ? c
                        : Color.FromArgb("#0E0E14");

                    var luminance = 0.2126 * bgBase.Red + 0.7152 * bgBase.Green + 0.0722 * bgBase.Blue;
                    var isLight = luminance > 0.55;

                    titleBar.ButtonForegroundColor = isLight ? Colors.Black : Colors.White;
                    titleBar.ButtonHoverForegroundColor = isLight ? Colors.Black : Colors.White;
                    titleBar.ButtonInactiveForegroundColor = isLight
                        ? global::Windows.UI.Color.FromArgb(120, 0, 0, 0)
                        : global::Windows.UI.Color.FromArgb(120, 255, 255, 255);
                    titleBar.ButtonHoverBackgroundColor = isLight
                        ? global::Windows.UI.Color.FromArgb(25, 0, 0, 0)
                        : global::Windows.UI.Color.FromArgb(30, 255, 255, 255);
                    titleBar.ButtonPressedBackgroundColor = isLight
                        ? global::Windows.UI.Color.FromArgb(50, 0, 0, 0)
                        : global::Windows.UI.Color.FromArgb(60, 255, 255, 255);
                }

                SyncTitleBarColors();

                Current?.RequestedThemeChanged += (_, _) => MainThread.BeginInvokeOnMainThread(SyncTitleBarColors);
                if (IPlatformApplication.Current?.Services.GetService<ThemeManager>() is { } tm)
                {
                    tm.OnThemeApplied += _ => MainThread.BeginInvokeOnMainThread(SyncTitleBarColors);
                    tm.OnThemeReset += () => MainThread.BeginInvokeOnMainThread(SyncTitleBarColors);
                    tm.OnSystemThemeSynced += () => MainThread.BeginInvokeOnMainThread(SyncTitleBarColors);
                }
            }
        };

        window.Destroying += (_, _) =>
        {
            Pages.MainPage.Current?.Dispose();
            InitiateGracefulBackgroundShutdown();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (IsShutdownCompleted)
            {
                return;
            }

            try
            {
                var vpnService = IPlatformApplication.Current?.Services.GetService<IVpnService>();
                var stopTask = vpnService?.StopVpnAsync() ?? Task.CompletedTask;
                _ = stopTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // ignored
            }
        };
#endif

        return window;
    }
}
