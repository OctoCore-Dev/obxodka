namespace obxodka.Pages;

public sealed partial class MainPage : ContentPage, IDisposable
{
    private readonly ApiService _apiService;
    private readonly IAppManager _appManager;
    private CancellationTokenSource? _vpnCts;
    public long RemainingSeconds { get; private set; }
    private string _activeTab = "";

    public IVpnService VpnService { get; }

#if ANDROID
    private readonly IAppUpdaterService _appUpdater;

    public MainPage(IVpnService vpnService, ApiService apiService, IAppManager appManager, IAppUpdaterService appUpdater)
#else
    public MainPage(IVpnService vpnService, ApiService apiService, IAppManager appManager)
#endif
    {
        InitializeComponent();
        VpnService = vpnService;
        _apiService = apiService;
        _appManager = appManager;
#if ANDROID
        _appUpdater = appUpdater;
#endif
        BindingContext = this;

        TabContentAuth.Initialize(this, _apiService);

        TabContentDelete.Initialize(this, _apiService);
        TabContentDelete.CancelRequested += (s, e) => _ = SwitchTabAsync("profile");
        TabContentDelete.AccountDeleted += async (s, e) => await PerformLogoutAsync();

        TabContentDevices.Initialize(this, _apiService);
        TabContentSplit.Initialize(this, _appManager);
        TabContentPayment.Initialize(this, _apiService);
        TabContentProfile.Initialize(this);
        TabContentProfile.LogoutRequested += async (s, e) => await HandleLogoutClickAsync();
        TabContentPayment.PaymentCompleted += async (s, e) =>
        {
            var session = await AuthManager.LoadSessionAsync();
            await SyncBalanceFromServerAsync(session);
            await SwitchTabAsync("profile");
        };
        TabContentPayment.PaymentCancelled += (s, e) => _ = SwitchTabAsync("profile");

        TabContentProfile.BuyTokensRequested += async (s, e) =>
        {
            var session = await AuthManager.LoadSessionAsync();
            _ = SwitchTabAsync("payment");
            TabContentPayment.LoadPaymentPage();
        };

        TabContentVpn.Initialize(this, VpnService, _apiService);

        VpnService.OnForceLogoutRequested -= HandleForceLogout;
        VpnService.OnForceLogoutRequested += HandleForceLogout;

        ApiService.OnUnauthorized -= HandleApiUnauthorized;
        ApiService.OnUnauthorized += HandleApiUnauthorized;
        App.AppResumed -= OnAppResumed;
        App.AppResumed += OnAppResumed;

        DesktopSidebar.NavTapped += (s, tab) => _ = SwitchTabAsync(tab);
        DesktopSidebar.LogoutTapped += async (s, e) => await HandleLogoutClickAsync();

        MobileBottomBar.NavTapped += (s, tab) => _ = SwitchTabAsync(tab);
    }

    public void Dispose()
    {
        _vpnCts?.Cancel();
        _vpnCts?.Dispose();
        _vpnCts = null;
        TabContentVpn.UnsubscribeEvents();
        VpnService.OnForceLogoutRequested -= HandleForceLogout;
        ApiService.OnUnauthorized -= HandleApiUnauthorized;
        GC.SuppressFinalize(this);
    }

    private bool _isLoggingOut;
    private void HandleForceLogout(string message)
    {
        if (_isLoggingOut)
        {
            return;
        }

        _isLoggingOut = true;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await DisplayAlertAsync("Внимание", message, "OK");
            await PerformLogoutAsync();
            _isLoggingOut = false;
        });
    }

    private void HandleApiUnauthorized() => HandleForceLogout("Сессия истекла. Пожалуйста, войдите снова.");

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Debug.WriteLine("=== [STARTUP] OnAppearing START ===");
        _ = AnimateSplashIconAsync();

        _ = Task.Run(async () =>
        {
            Debug.WriteLine("=== [STARTUP] Task.Run START ===");
            try
            {
                Debug.WriteLine("=== [STARTUP] Fetching Bridge URL ===");
                var activeHost = await DiscoveryService.GetActiveBridgeUrlAsync();
                Debug.WriteLine($"=== [STARTUP] Bridge URL Fetched: {activeHost} ===");
                if (!string.IsNullOrEmpty(activeHost))
                {
                    AppConfig.ApiBaseUrl = $"https://{activeHost}/";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DISCOVERY ERROR] {ex.Message}");
            }

            Debug.WriteLine("=== [STARTUP] Loading Session ===");
            UserSession? session = null;
            try
            {
                session = await AuthManager.LoadSessionAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"=== [STARTUP] AuthManager ERROR: {ex.Message} ===");
            }
            Debug.WriteLine($"=== [STARTUP] Session Loaded: IsLoggedIn={session?.IsLoggedIn} ===");

            _ = Task.Run(async () =>
            {
#if ANDROID
                try
                {
                    Debug.WriteLine("=== [STARTUP] Checking for updates ===");
                    await _appUpdater.CheckForUpdatesAsync();
                }
                catch { }
#endif
                try
                {
                    Debug.WriteLine("=== [STARTUP] Getting installed apps ===");
                    await _appManager.GetInstalledAppsAsync();
                }
                catch { }
            });

            Debug.WriteLine("=== [STARTUP] Calling BeginInvokeOnMainThread ===");
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                Debug.WriteLine("=== [STARTUP] Inside BeginInvokeOnMainThread ===");

                if (SplashOverlay.IsVisible)
                {
                    await Task.Delay(3000);
                    _ = SplashOverlay.FadeToAsync(0, 500, Easing.CubicInOut);
                    await Task.Delay(200);
                    SplashOverlay.IsVisible = false;
                }

                Debug.WriteLine("=== [STARTUP] Splash hidden ===");

                if (session is { IsLoggedIn: true, JwtToken: not null })
                {
                    Debug.WriteLine("=== [STARTUP] Switching to App (VPN) ===");
                    await SwitchToAppAfterAuthAsync();
                    if (App.PendingTileAction)
                    {
                        App.PendingTileAction = false;
                    }
                }
                else
                {
                    Debug.WriteLine("=== [STARTUP] Switching to Auth ===");
                    DesktopSidebar.HideSidebar();
                    MobileBottomBar.HideSidebar();
                    _ = SwitchTabAsync("auth");
                    _ = TabContentAuth.PlayEntranceAnimationAsync();
                }
                Debug.WriteLine("=== [STARTUP] BeginInvokeOnMainThread END ===");
            });
        });
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        SplashOverlay.IsVisible = false;
    }

    private async Task AnimateSplashIconAsync()
    {
        if (SplashIcon == null || !SplashIcon.IsVisible)
        {
            return;
        }

        while (SplashOverlay.IsVisible)
        {
            _ = await SplashIcon.ScaleToAsync(1.1, 800, Easing.CubicInOut);
            _ = await SplashIcon.ScaleToAsync(1.0, 800, Easing.CubicInOut);
        }
    }

    public async Task SwitchToAppAfterAuthAsync()
    {
        var session = await AuthManager.LoadSessionAsync();
        _ = TabContentAuth.FadeToAsync(0, 300);
        await Task.Delay(300);

        TabContentVpn.Initialize(this, VpnService, _apiService);
        _ = SwitchTabAsync("vpn");

        _ = DesktopSidebar.PlayEntranceAnimationAsync();
        _ = MobileBottomBar.PlayEntranceAnimationAsync();

        await SyncBalanceFromServerAsync(session);
    }

    private async Task HandleLogoutClickAsync()
    {
        var confirm = await DisplayAlertAsync("Выход", "Выйти из аккаунта?", "Да", "Отмена");
        if (confirm)
        {
            await PerformLogoutAsync();
        }
    }

    private async Task PerformLogoutAsync()
    {
        _vpnCts?.Cancel();
        TabContentVpn.UnsubscribeEvents();
        _ = Task.Run(async () =>
        {
            try
            { await VpnService.StopVpnAsync(); }
            catch { }
        });

        await AuthManager.ClearSessionAsync();
        AppConfig.ApiBaseUrl = "https://obxodka.one/";

        MainThread.BeginInvokeOnMainThread(() =>
        {
            DesktopSidebar.HideSidebar();
            MobileBottomBar.HideSidebar();
            _ = SwitchTabAsync("auth");
        });
    }


    public async Task SwitchTabAsync(string tab)
    {
        if (_activeTab == tab)
        {
            return;
        }

        var prevTab = _activeTab;
        _activeTab = tab;

        DesktopSidebar.UpdateActiveTab(tab);
        MobileBottomBar.UpdateActiveTab(tab);

        var outgoing = GetTabContent(prevTab);
        var incoming = GetTabContent(tab);
        await UIAnimations.SwitchViewAsync(outgoing, incoming);

        switch (tab)
        {
            case "vpn":
                _ = TabContentVpn.PlayEntranceAnimationAsync();
                break;
            case "battery":
                TabContentBattery.OnAppearing();
                _ = TabContentBattery.PlayEntranceAnimationAsync();
                break;
            case "profile":
                var session = await AuthManager.LoadSessionAsync();
                TabContentProfile.UpdateProfileInfo(session);
                TabContentProfile.UpdateBalance(RemainingSeconds);
                _ = TabContentProfile.PlayEntranceAnimationAsync();
                break;
            case "devices":
                _ = TabContentDevices.PlayEntranceAnimationAsync();
                _ = TabContentDevices.LoadDevicesAsync();
                break;
            case "split":
                _ = TabContentSplit.LoadSplitAppsAsync();
                break;
            case "auth":
                _ = TabContentAuth.PlayEntranceAnimationAsync();
                break;
            case "payment":
                _ = TabContentPayment.PlayEntranceAnimationAsync();
                break;
            default:
                break;
        }
    }

    private View? GetTabContent(string? tab) => tab switch
    {
        "auth" => TabContentAuth,
        "vpn" => TabContentVpn,
        "battery" => TabContentBattery,
        "profile" => TabContentProfile,
        "devices" => TabContentDevices,
        "payment" => TabContentPayment,
        "split" => TabContentSplit,
        "delete" => TabContentDelete,
        _ => null
    };

    public void NotifyVpnConnected()
    {
        DesktopSidebar.UpdateVpnStatus(true);
        if (_vpnCts == null && RemainingSeconds > 0)
        {
            _vpnCts = new CancellationTokenSource();
            _ = ConsumeTimeLoopAsync(_vpnCts.Token);
        }
    }

    public void NotifyVpnDisconnected()
    {
        DesktopSidebar.UpdateVpnStatus(false);
        _vpnCts?.Cancel();
        _vpnCts = null;
    }

    private void UpdateBalanceUI()
    {
        var hours = RemainingSeconds / 3600;
        var minutes = RemainingSeconds % 3600 / 60;
        var timeText = $"{hours}ч {minutes:D2}м";
        TabContentVpn.UpdateBalanceUI(timeText);
        TabContentProfile.UpdateBalance(RemainingSeconds);
    }

    private void OnAppResumed()
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (_activeTab != "auth")
            {
                await SyncBalanceFromServerAsync();
            }
        });
    }

    private async Task SyncBalanceFromServerAsync(UserSession? session = null)
    {
        session ??= await AuthManager.LoadSessionAsync();
        if (session == null)
        {
            return;
        }

        var (success, profile, error) = await _apiService.GetProfileAsync();
        if (success && profile != null)
        {
            session.SubscriptionUntil = profile.SubscriptionUntil;
            await AuthManager.SaveSessionAsync(session);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                RemainingSeconds = profile.BalanceSeconds > 0
                    ? profile.BalanceSeconds
                    : (profile.SubscriptionUntil.HasValue
                        ? Math.Max(0, (long)(profile.SubscriptionUntil.Value.ToUniversalTime() - DateTime.UtcNow).TotalSeconds)
                        : 0);
                UpdateBalanceUI();
                TabContentVpn.UpdateBalanceUI($"{RemainingSeconds / 3600}ч {RemainingSeconds % 3600 / 60:D2}м", Views.VpnView.FormatBytes(profile.TotalBytesUsed));
            });
        }
        else
        {
            // Do nothing on generic network error.
            // Actual 401 Unauthorized errors are handled by ApiService.OnUnauthorized.
            Debug.WriteLine($"[SYNC] Failed to fetch profile: {error}");
        }
    }

    private async Task ConsumeTimeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && RemainingSeconds > 0)
        {
            try
            {
                await Task.Delay(1000, ct);
                RemainingSeconds--;
                MainThread.BeginInvokeOnMainThread(UpdateBalanceUI);
            }
            catch { break; }
        }
        if (RemainingSeconds <= 0)
        {
            await VpnService.StopVpnAsync();
            await MainThread.InvokeOnMainThreadAsync(() =>
                DisplayAlertAsync("Лимит", "Пополните баланс", "ОК"));
        }
    }

}
