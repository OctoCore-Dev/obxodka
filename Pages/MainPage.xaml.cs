namespace obxodka.Pages;

public sealed partial class MainPage : ContentPage, IDisposable
{
    private readonly ApiService _apiService;
    private readonly IAppManager _appManager;
    private readonly IAppUpdaterService? _appUpdaterService;
    private CancellationTokenSource? _vpnCts;
    public long RemainingSeconds { get; private set; }
    private string _activeTab = "";
    private bool _isLoggingOut;

    public IVpnService VpnService { get; }

    public MainPage(
        IVpnService vpnService,
        ApiService apiService,
        IAppManager appManager,
        IAppUpdaterService? appUpdaterService = null)
    {
        InitializeComponent();
        VpnService = vpnService;
        _apiService = apiService;
        _appManager = appManager;
        _appUpdaterService = appUpdaterService;
        BindingContext = this;

        TabContentAuth.Initialize(this, _apiService);

        TabContentDelete.Initialize(this, _apiService);
        TabContentDelete.CancelRequested += OnDeleteCancelRequested;
        TabContentDelete.AccountDeleted += OnAccountDeletedAsync;

        TabContentDevices.Initialize(this, _apiService);
        TabContentSplit.Initialize(this, _appManager);
        TabContentPayment.Initialize(this, _apiService);
        TabContentProfile.Initialize(this);
        TabContentFriends.Initialize(_apiService);
        TabContentFriends.BackRequested += (_, _) => _ = SwitchTabAsync("profile");
        TabContentMesh.Initialize(_apiService);

        TabContentProfile.LogoutRequested += OnProfileLogoutRequestedAsync;
        TabContentProfile.BuyTokensRequested += OnBuyTokensRequested;
        TabContentProfile.FriendsRequested += (_, _) => _ = SwitchTabAsync("friends");

        TabContentPayment.PaymentCompleted += OnPaymentCompletedAsync;
        TabContentPayment.PaymentCancelled += OnPaymentCancelled;

        TabContentVpn.Initialize(this, VpnService, _apiService);

        VpnService.OnForceLogoutRequested -= HandleForceLogout;
        VpnService.OnForceLogoutRequested += HandleForceLogout;

        ApiService.OnUnauthorized -= HandleApiUnauthorized;
        ApiService.OnUnauthorized += HandleApiUnauthorized;

        App.AppResumed -= OnAppResumed;
        App.AppResumed += OnAppResumed;

        Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged;
        Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;

        DesktopSidebar.NavTapped += OnSidebarNavTapped;
        DesktopSidebar.LogoutTapped += OnSidebarLogoutTappedAsync;

        MobileBottomBar.NavTapped += OnBottomBarNavTapped;
    }

    public void Dispose()
    {
        _vpnCts?.Cancel();
        _vpnCts?.Dispose();
        _vpnCts = null;

        TabContentVpn.UnsubscribeEvents();
        VpnService.OnForceLogoutRequested -= HandleForceLogout;
        ApiService.OnUnauthorized -= HandleApiUnauthorized;
        App.AppResumed -= OnAppResumed;
        Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged;

        TabContentDelete.CancelRequested -= OnDeleteCancelRequested;
        TabContentDelete.AccountDeleted -= OnAccountDeletedAsync;
        TabContentProfile.LogoutRequested -= OnProfileLogoutRequestedAsync;
        TabContentProfile.BuyTokensRequested -= OnBuyTokensRequested;
        TabContentProfile.FriendsRequested -= (_, _) => _ = SwitchTabAsync("friends");
        TabContentPayment.PaymentCompleted -= OnPaymentCompletedAsync;
        TabContentPayment.PaymentCancelled -= OnPaymentCancelled;
        DesktopSidebar.NavTapped -= OnSidebarNavTapped;
        DesktopSidebar.LogoutTapped -= OnSidebarLogoutTappedAsync;
        MobileBottomBar.NavTapped -= OnBottomBarNavTapped;

        GC.SuppressFinalize(this);
    }

    private void OnDeleteCancelRequested(object? sender, EventArgs e) =>
        _ = SwitchTabAsync("profile");

    private async void OnAccountDeletedAsync(object? sender, EventArgs e) =>
        await PerformLogoutAsync();

    private async void OnProfileLogoutRequestedAsync(object? sender, EventArgs e) =>
        await HandleLogoutClickAsync();

    private void OnBuyTokensRequested(object? sender, EventArgs e)
    {
        _ = SwitchTabAsync("payment");
        TabContentPayment.LoadPaymentPage();
    }

    private async void OnPaymentCompletedAsync(object? sender, EventArgs e)
    {
        var session = await AuthManager.LoadSessionAsync();
        await SyncBalanceFromServerAsync(session);
        await SwitchTabAsync("profile");
    }

    private void OnPaymentCancelled(object? sender, EventArgs e) =>
        _ = SwitchTabAsync("profile");

    private void OnSidebarNavTapped(object? sender, string tab) =>
        _ = SwitchTabAsync(tab);

    private async void OnSidebarLogoutTappedAsync(object? sender, EventArgs e) =>
        await HandleLogoutClickAsync();

    private void OnBottomBarNavTapped(object? sender, string tab) =>
        _ = SwitchTabAsync(tab);

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

    private void HandleApiUnauthorized() =>
        HandleForceLogout("Сессия истекла. Пожалуйста, войдите снова.");

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = AnimateSplashIconAsync();

        _ = Task.Run(async () =>
        {
            try
            {
                var activeHost = await DiscoveryService.GetActiveBridgeUrlAsync();
                if (!string.IsNullOrEmpty(activeHost))
                {
                    AppConfig.ApiBaseUrl = $"https://{activeHost}/";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DISCOVERY ERROR] {ex.Message}");
            }

            UserSession? session = null;
            try
            {
                session = await AuthManager.LoadSessionAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AUTH LOAD ERROR] {ex.Message}");
            }



            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (SplashOverlay.IsVisible)
                {
                    await Task.Delay(2000);
                    _ = SplashOverlay.FadeToAsync(0, 450, Easing.CubicInOut);
                    await Task.Delay(200);
                    SplashOverlay.IsVisible = false;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (_appUpdaterService is not null)
                        {
                            await Task.Delay(2500);
                            await _appUpdaterService.CheckForUpdatesAsync(manualCheck: false);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[AUTO UPDATE CHECK] {ex.Message}");
                    }
                });

                if (session is { IsLoggedIn: true, JwtToken.Length: > 0 })
                {
                    await SwitchToAppAfterAuthAsync();
                    if (App.PendingTileAction)
                    {
                        App.PendingTileAction = false;
                    }
                }
                else
                {
                    DesktopSidebar.HideSidebar();
                    MobileBottomBar.HideSidebar();
                    _ = SwitchTabAsync("auth");
                    _ = TabContentAuth.PlayEntranceAnimationAsync();
                }
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
        if (SplashIcon is null || !SplashIcon.IsVisible)
        {
            return;
        }

        while (SplashOverlay.IsVisible)
        {
            _ = await SplashIcon.ScaleToAsync(1.08, 800, Easing.CubicInOut);
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

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(800);
                _ = await _appManager.GetInstalledAppsAsync();
            }
            catch { }
        });

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
            {
                await VpnService.StopVpnAsync();
            }
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
            case "configuration":
                TabContentConfiguration.OnAppearing();
                _ = TabContentConfiguration.PlayEntranceAnimationAsync();
                break;
            case "profile":
                var session = await AuthManager.LoadSessionAsync();
                TabContentProfile.UpdateProfileInfo(session);
                TabContentProfile.UpdateBalance(RemainingSeconds);
                _ = TabContentProfile.PlayEntranceAnimationAsync();
                break;
            case "friends":
                _ = TabContentFriends.OnAppearingAsync();
                break;
            case "mesh":
                TabContentMesh.Initialize(_apiService);
                TabContentMesh.ForceLayoutWidth();
                _ = TabContentMesh.PlayEntranceAnimationAsync();
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
            case "delete":
                _ = TabContentDelete.PlayEntranceAnimationAsync();
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
        "configuration" => TabContentConfiguration,
        "profile" => TabContentProfile,
        "friends" => TabContentFriends,
        "mesh" => TabContentMesh,
        "devices" => TabContentDevices,
        "payment" => TabContentPayment,
        "split" => TabContentSplit,
        "delete" => TabContentDelete,
        _ => null
    };

    public void NotifyVpnConnected()
    {
        DesktopSidebar.UpdateVpnStatus(true);
        if (_vpnCts is null && RemainingSeconds > 0)
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
        var timeText = TimeFormatHelper.FormatSeconds(RemainingSeconds, false);
        TabContentVpn.UpdateBalanceUI(timeText);
        TabContentProfile.UpdateBalance(RemainingSeconds);
    }

    private void OnAppResumed() =>
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (_activeTab != "auth")
            {
                await SyncBalanceFromServerAsync();
            }
        });

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        if (e.NetworkAccess == NetworkAccess.Internet)
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (_activeTab != "auth")
                {
                    await SyncBalanceFromServerAsync();
                }
            });
        }
    }

    private async Task SyncBalanceFromServerAsync(UserSession? session = null)
    {
        session ??= await AuthManager.LoadSessionAsync();
        if (session is null || string.IsNullOrEmpty(session.JwtToken))
        {
            return;
        }

        var (success, profile, error) = await _apiService.GetProfileAsync();
        if (success && profile is not null)
        {
            session.SubscriptionUntil = profile.SubscriptionUntil;
            session.BalanceSeconds = profile.BalanceSeconds;
            await AuthManager.SaveSessionAsync(session);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                RemainingSeconds = profile.BalanceSeconds > 0
                    ? profile.BalanceSeconds
                    : (profile.SubscriptionUntil is { } until
                        ? Math.Max(0, (long)(until.ToUniversalTime() - DateTime.UtcNow).TotalSeconds)
                        : 0);

                UpdateBalanceUI();
                TabContentVpn.UpdateBalanceUI(
                    TimeFormatHelper.FormatSeconds(RemainingSeconds, false),
                    Views.VpnView.FormatBytes(profile.TotalBytesUsed));
            });
        }
        else
        {
            Debug.WriteLine($"[SYNC ERROR] {error}");
        }
    }

    private async Task ConsumeTimeLoopAsync(CancellationToken ct)
    {
        var syncCounter = 0;
        while (!ct.IsCancellationRequested && RemainingSeconds > 0)
        {
            try
            {
                await Task.Delay(1000, ct);
                RemainingSeconds = Math.Max(0, RemainingSeconds - 1);
                MainThread.BeginInvokeOnMainThread(UpdateBalanceUI);

                syncCounter++;
                if (syncCounter >= 30)
                {
                    syncCounter = 0;
                    _ = SyncBalanceFromServerAsync();
                }
            }
            catch
            {
                break;
            }
        }

        if (RemainingSeconds <= 0)
        {
            await VpnService.StopVpnAsync();
            await MainThread.InvokeOnMainThreadAsync(() =>
                DisplayAlertAsync("Лимит", "Время действия тарифа закончилось. Пополните баланс.", "ОК"));
        }
    }
}
