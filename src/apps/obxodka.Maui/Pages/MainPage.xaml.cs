namespace obxodka.Pages;

public sealed partial class MainPage : IDisposable
{
    private static readonly string[] s_mainMobileTabs = ["vpn", "profile", "devices", "split", "configuration"];
    private static int s_memoryTrimScheduled;

    private readonly ApiService _apiService;
    private readonly IAppManager _appManager;
    private readonly IAppUpdaterService? _appUpdaterService;
    private readonly IQrScannerService? _qrScanner;
    private readonly ThemeManager _themeManager;
    private Color _activeParticleAccent = Colors.Transparent;
    private Color _activeParticlePrimary = Colors.Transparent;
    private List<string>? _activeParticleSpritePaths;
    private string _activeTab = "";
    private FrameSlice? _activeThemeFrameSlice;
    private string? _activeThemeScreenFramePath;
    private ThemeVfx? _activeVfx;
    private bool _hasThemeCornerDecorations;
    private bool _isDecorationsFaded;
    private int _forceLogoutActive;
    private int _isSyncingBalance;
    private bool _isThemeVideoActive;
    private bool _isVpnTabInitialized;

    private AdaptiveViewport _currentViewport;
    private bool? _isNavChromeVisible;
    private bool _isOverlaySidebar;
    private bool? _isWideLayout;
    private bool _isWindowActive = true;
    private double _lastAppliedBottomInset = -1;
    private ThemeAppliedEventArgs? _lastAppliedThemeArgs;

    private double _lastAppliedTopInset = -1;
    private long _lastEngineBytesReceived;
    private long _lastEngineBytesSent;
    private long _lastEngineTrafficTicks;
    private int _startupHandled;

    private AuthView? _tabContentAuth;
    private ConfigurationView? _tabContentConfiguration;
    private DeleteAccountView? _tabContentDelete;
    private DevicesView? _tabContentDevices;
    private PaymentView? _tabContentPayment;
    private ProfileView? _tabContentProfile;
    private SplitView? _tabContentSplit;
    private ThemeStoreView? _tabContentThemeStore;
    private string _lastThemeStoreTab = "community";
    private string _lastThemeStoreFilter = "all";
    private string _lastThemeStoreSearch = string.Empty;
    private double _lastThemeStoreScrollY;
    private VpnView? _tabContentVpn;
    private CancellationTokenSource? _vpnCts;

    public MainPage(
        IVpnService vpnService,
        ApiService apiService,
        IAppManager appManager,
        ThemeManager themeManager,
        IAppUpdaterService? appUpdaterService = null,
        IQrScannerService? qrScanner = null)
    {
        Current = this;
        InitializeComponent();

        VpnService = vpnService;
        _apiService = apiService;
        _appManager = appManager;
        _themeManager = themeManager;
        _appUpdaterService = appUpdaterService;
        _qrScanner = qrScanner;
        BindingContext = this;
        DesktopSidebar.Initialize(_appUpdaterService);

        _themeManager.OnThemeTransitionStarting += ShowThemeTransitionAsync;
        _themeManager.OnThemeApplied += HandleThemeApplied;
        _themeManager.OnThemeReset += HandleThemeReset;
        _themeManager.OnSystemThemeSynced += HandleSystemThemeSynced;
        _themeManager.VideoEnabledChanged += OnThemeVideoEnabledChanged;
        _themeManager.ParticlesEnabledChanged += OnThemeParticlesEnabledChanged;
        _themeManager.FramesEnabledChanged += OnThemeFramesEnabledChanged;
        _themeManager.AlwaysPlayVideoChanged += OnAlwaysPlayVideoChanged;
        _themeManager.ParallaxEnabledChanged += OnThemeParallaxEnabledChanged;
        _themeManager.CardOpacityChanged += OnCardOpacityChanged;
        _themeManager.GlowIntensityChanged += OnGlowIntensityChanged;

        //создаем только одну стартовую вкладку до восстановления темы, остальные загружаем лениво
        var isLikelyLogged = PlatformServices.Preferences.Get(AppPreferenceKeys.UserIsLogged, false);
        var initialTab = isLikelyLogged ? "vpn" : "auth";
        var initialView = EnsureTabCreated(initialTab);

        _themeManager.RestoreActiveThemeAtStartup();
        UpdateAllViewsOpacity();

        if (!SplashOverlay.IsVisible)
        {
            _activeTab = initialTab;
            initialView.Opacity = 1.0;
            initialView.IsVisible = true;
            if (isLikelyLogged)
            {
                RemainingSeconds = ReadCachedRemainingSeconds();
                UpdateBalanceUi();
                DesktopSidebar.UpdateActiveTab("vpn");
                MobileBottomBar.UpdateActiveTab("vpn");
            }
            else
            {
                DesktopSidebar.HideSidebar();
                MobileBottomBar.HideSidebar();
            }
        }

        RefreshAdaptiveLayout(forceApply: true);

        VpnService.OnForceLogoutRequested -= HandleForceLogout;
        VpnService.OnForceLogoutRequested += HandleForceLogout;
        VpnService.OnStateChanged -= OnVpnStateChangedForTheme;
        VpnService.OnStateChanged += OnVpnStateChangedForTheme;

        OctopusEngine.Current.OnTrafficUpdated -= OnEngineTrafficUpdated;
        OctopusEngine.Current.OnTrafficUpdated += OnEngineTrafficUpdated;

        ApiService.OnUnauthorized -= HandleApiUnauthorized;
        ApiService.OnUnauthorized += HandleApiUnauthorized;

        App.AppResumed -= OnAppResumed;
        App.AppResumed += OnAppResumed;

        App.DeepLinkLoginReceived -= OnDeepLinkLoginReceived;
        App.DeepLinkLoginReceived += OnDeepLinkLoginReceived;

        App.BackgroundActionReceived -= OnBackgroundActionReceived;
        App.BackgroundActionReceived += OnBackgroundActionReceived;

        App.WindowActivated -= OnWindowActivated;
        App.WindowActivated += OnWindowActivated;
        App.WindowDeactivated -= OnWindowDeactivated;
        App.WindowDeactivated += OnWindowDeactivated;
        App.WindowVisibilityChanged -= OnWindowVisibilityChanged;
        App.WindowVisibilityChanged += OnWindowVisibilityChanged;

        Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged;
        Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;

        DesktopSidebar.NavTapped += OnSidebarNavTapped;
        DesktopSidebar.LogoutTapped += OnSidebarLogoutTappedAsync;
        DesktopSidebar.ExpandedChanged += OnSidebarExpandedChanged;
        DesktopSidebar.SidebarWidthChanged += OnSidebarWidthChanged;
        MobileBottomBar.NavTapped += OnBottomBarNavTapped;

        SafeAreaHelper.InsetsChanged -= OnSafeAreaInsetsChanged;
        SafeAreaHelper.InsetsChanged += OnSafeAreaInsetsChanged;
        if (SafeAreaHelper.TopInset > 0 || SafeAreaHelper.BottomInset > 0)
        {
            ApplySafeArea(SafeAreaHelper.TopInset, SafeAreaHelper.BottomInset);
        }

#if WINDOWS
        ThemeDecorationsContainer.HandlerChanged += (_, _) =>
        {
            WinUiMainPageHelper.DisableHitTest(ThemeDecorationsContainer);
            WinUiMainPageHelper.ApplyClip(ThemeDecorationsContainer);
        };
        ThemeBackgroundViewport.HandlerChanged += (_, _) =>
        {
            WinUiMainPageHelper.DisableHitTest(ThemeBackgroundViewport);
            WinUiMainPageHelper.ApplyClip(ThemeBackgroundViewport);
        };
        ThemeBackgroundMover.HandlerChanged += (_, _) => WinUiMainPageHelper.DisableHitTest(ThemeBackgroundMover);
        ThemeBackgroundImage.HandlerChanged += (_, _) => WinUiMainPageHelper.DisableHitTest(ThemeBackgroundImage);
        ThemeBackgroundVideo.HandlerChanged += (_, _) => WinUiMainPageHelper.ConfigureVideoPlayer(ThemeBackgroundVideo);
        ThemeBackgroundVideoDimmer.HandlerChanged += (_, _) => WinUiMainPageHelper.DisableHitTest(ThemeBackgroundVideoDimmer);
        ThisPage.HandlerChanged += (_, _) =>
        {
            WinUiMainPageHelper.ApplyClip(ThisPage);
            WinUiMainPageHelper.AttachPointerHandlers(
                ThisPage,
                (x, y) => HandlePointerInteraction(x, y, ThisPage.Width, ThisPage.Height),
                (x, y) =>
                {
                    ThemeParticles?.TriggerPointerBurst((float)x, (float)y);
                    if (!_isWindowActive)
                    {
                        OnWindowActivated();
                    }
                },
                (x, y) =>
                {
                    var w = ThisPage.Width;
                    var h = ThisPage.Height;
                    if (w <= 0 || h <= 0 || x <= 4 || y <= 4 || x >= w - 4 || y >= h - 4)
                    {
                        RestoreDecorationsOpacity();
                    }
                });
        };
#endif
    }

    public static MainPage? Current { get; private set; }
    public long RemainingSeconds { get; private set; }
    public IVpnService VpnService { get; }

    //отложенное уплотнение кучи после выгрузки тяжелых ресурсов темы или временных вкладок
    internal static void ScheduleMemoryTrim()
    {
        if (Interlocked.Exchange(ref s_memoryTrimScheduled, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350).ConfigureAwait(false);
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
#pragma warning disable RS0030 //явное уплотнение loh после выгрузки тяжелых ресурсов темы
                GC.Collect(2, GCCollectionMode.Optimized, false);
#pragma warning restore RS0030
            }
            catch
            {
                //игнорируем ошибки фонового уплотнения памяти
            }
            finally
            {
                _ = Interlocked.Exchange(ref s_memoryTrimScheduled, 0);
            }
        });
    }

    public void Dispose()
    {
        _vpnCts?.Cancel();
        _vpnCts?.Dispose();
        _vpnCts = null;

        App.WindowActivated -= OnWindowActivated;
        App.WindowDeactivated -= OnWindowDeactivated;
        App.WindowVisibilityChanged -= OnWindowVisibilityChanged;
        App.DeepLinkLoginReceived -= OnDeepLinkLoginReceived;
        App.BackgroundActionReceived -= OnBackgroundActionReceived;

        _tabContentVpn?.UnsubscribeEvents();
        VpnService.OnForceLogoutRequested -= HandleForceLogout;
        VpnService.OnStateChanged -= OnVpnStateChangedForTheme;
        OctopusEngine.Current.OnTrafficUpdated -= OnEngineTrafficUpdated;
        ApiService.OnUnauthorized -= HandleApiUnauthorized;
        App.AppResumed -= OnAppResumed;
        Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged;

        _themeManager.OnThemeTransitionStarting -= ShowThemeTransitionAsync;
        _themeManager.OnThemeApplied -= HandleThemeApplied;
        _themeManager.OnThemeReset -= HandleThemeReset;
        _themeManager.OnSystemThemeSynced -= HandleSystemThemeSynced;
        _themeManager.VideoEnabledChanged -= OnThemeVideoEnabledChanged;
        _themeManager.ParticlesEnabledChanged -= OnThemeParticlesEnabledChanged;
        _themeManager.FramesEnabledChanged -= OnThemeFramesEnabledChanged;
        _themeManager.AlwaysPlayVideoChanged -= OnAlwaysPlayVideoChanged;
        _themeManager.ParallaxEnabledChanged -= OnThemeParallaxEnabledChanged;
        _themeManager.CardOpacityChanged -= OnCardOpacityChanged;
        _themeManager.GlowIntensityChanged -= OnGlowIntensityChanged;

        if (_tabContentConfiguration is not null)
        {
            _tabContentConfiguration.ThemesRequested -= OnConfigurationThemesRequested;
            _tabContentConfiguration.Dispose();
        }

        if (_tabContentThemeStore is not null)
        {
            _tabContentThemeStore.BackRequested -= OnThemeStoreBackRequested;
            _tabContentThemeStore.Cleanup();
        }

        _tabContentVpn?.Dispose();
        ThemeParticles?.Dispose();
        ThemeAudioService.ReleaseMediaResources();
        if (_tabContentDelete is not null)
        {
            _tabContentDelete.CancelRequested -= OnDeleteCancelRequested;
            _tabContentDelete.AccountDeleted -= OnAccountDeletedAsync;
        }

        if (_tabContentProfile is not null)
        {
            _tabContentProfile.LogoutRequested -= OnProfileLogoutRequestedAsync;
            _tabContentProfile.BuyTokensRequested -= OnBuyTokensRequestedAsync;
            _tabContentProfile.BalanceRefreshRequested -= OnProfileBalanceRefreshRequestedAsync;
        }

        if (_tabContentPayment is not null)
        {
            _tabContentPayment.PaymentCompleted -= OnPaymentCompletedAsync;
            _tabContentPayment.PaymentCancelled -= OnPaymentCancelled;
            _tabContentPayment.StopPaymentPolling();
        }

        DesktopSidebar.NavTapped -= OnSidebarNavTapped;
        DesktopSidebar.LogoutTapped -= OnSidebarLogoutTappedAsync;
        DesktopSidebar.ExpandedChanged -= OnSidebarExpandedChanged;
        DesktopSidebar.SidebarWidthChanged -= OnSidebarWidthChanged;
        MobileBottomBar.NavTapped -= OnBottomBarNavTapped;
        SafeAreaHelper.InsetsChanged -= OnSafeAreaInsetsChanged;
        if (Current == this)
        {
            Current = null;
        }

        GC.SuppressFinalize(this);
    }

    private void HandleApiUnauthorized() =>
        HandleForceLogout("Сессия истекла. Пожалуйста, войдите снова.");

    private void OnDeleteCancelRequested(object? sender, EventArgs e) =>
        _ = SwitchTabAsync("profile");

    private void OnConfigurationThemesRequested(object? sender, EventArgs e) =>
        _ = SwitchTabAsync("themes");

    private void OnThemeStoreBackRequested(object? sender, EventArgs e) =>
        _ = SwitchTabAsync("configuration");

    private void OnPaymentCancelled(object? sender, EventArgs e) =>
        _ = SwitchTabAsync("profile");

    private void OnSidebarNavTapped(object? sender, string tab) =>
        _ = SwitchTabAsync(tab);

    private void OnBottomBarNavTapped(object? sender, string tab) =>
        _ = SwitchTabAsync(tab);

    private async void OnAccountDeletedAsync(object? sender, EventArgs e)
    {
        try
        {
            await PerformLogoutAsync();
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[MAINPAGE ACCOUNT DELETED ERROR] {ex.Message}", ex);
        }
    }

    private async void OnProfileLogoutRequestedAsync(object? sender, EventArgs e)
    {
        try
        {
            await HandleLogoutClickAsync();
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[MAINPAGE LOGOUT ERROR] {ex.Message}", ex);
        }
    }

    private async void OnProfileBalanceRefreshRequestedAsync(object? sender, EventArgs e)
    {
        try
        {
            await SyncBalanceFromServerAsync();
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[MAINPAGE BALANCE REFRESH ERROR] {ex.Message}");
        }
    }

    private async void OnBuyTokensRequestedAsync(object? sender, EventArgs e)
    {
        try
        {
            await SwitchTabAsync("payment");
            _tabContentPayment?.LoadPaymentPage();
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[MAINPAGE PAYMENT NAV ERROR] {ex.Message}", ex);
        }
    }

    private async void OnPaymentCompletedAsync(object? sender, EventArgs e)
    {
        try
        {
            var session = await AuthManager.LoadSessionAsync();
            await SyncBalanceFromServerAsync(session);
            await SwitchTabAsync("profile");
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[MAINPAGE PAYMENT COMPLETED ERROR] {ex.Message}", ex);
        }
    }

    private async void OnSidebarLogoutTappedAsync(object? sender, EventArgs e)
    {
        try
        {
            await HandleLogoutClickAsync();
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[MAINPAGE SIDEBAR LOGOUT ERROR] {ex.Message}", ex);
        }
    }

    private void OnDeepLinkLoginReceived(string email, string code) =>
        MainThread.BeginInvokeOnMainThread(async void () =>
        {
            try
            {
                App.PendingDeepLinkLogin = null;
                var session = await AuthManager.LoadSessionAsync();
                if (session is { IsLoggedIn: true, JwtToken.Length: > 0 })
                {
                    return;
                }

                if (_activeTab != "auth")
                {
                    DesktopSidebar.HideSidebar();
                    MobileBottomBar.HideSidebar();
                    await SwitchTabAsync("auth");
                }

                if (_tabContentAuth is not null)
                {
                    await _tabContentAuth.HandleDeepLinkLoginAsync(email, code);
                }
            }
            catch
            {
                // ignored
            }
        });

    private void HandleForceLogout(string message)
    {
        if (_activeTab == "auth")
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _forceLogoutActive, 1, 0) != 0)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(async void () =>
        {
            try
            {
                if (_activeTab == "auth")
                {
                    return;
                }

                //сначала очищаем сессию и переводим на экран входа чтобы фоновые запросы больше не вызывали 401
                await PerformLogoutAsync();
                await DisplayAlertAsync("Внимание", message, "OK");
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"[FORCE LOGOUT ERROR] {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _forceLogoutActive, 0);
            }
        });
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        if (Interlocked.CompareExchange(ref _startupHandled, 1, 0) != 0)
        {
            return;
        }

        if (SplashOverlay.IsVisible)
        {
            _ = AnimateSplashIconAsync();
        }

        _ = Task.Run(async () =>
        {
            UserSession? session = null;
            try
            {
                session = await AuthManager.LoadSessionAsync();
            }
            catch (Exception ex)
            {
                AppLogger.LogError("[AUTH LOAD ERROR]", ex);
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    if (_appUpdaterService is not null)
                    {
                        await Task.Delay(2500);
                        await _appUpdaterService.CheckForUpdatesAsync();
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"[AUTO UPDATE CHECK] {ex.Message}");
                }
            });

            MainThread.BeginInvokeOnMainThread(async void () =>
            {
                try
                {
                    if (SplashOverlay.IsVisible)
                    {
                        await Task.Delay(1000);
                        _ = SplashOverlay.FadeToAsync(0, 350, Easing.CubicInOut);
                        await Task.Delay(360);
                        SplashOverlay.InputTransparent = true;
                        SplashOverlay.IsVisible = false;
#if WINDOWS
                        WinUiMainPageHelper.DisableHitTest(SplashOverlay);
#endif
                    }

                    if (session is { IsLoggedIn: true, JwtToken.Length: > 0 })
                    {
                        if (_activeTab == "vpn")
                        {
                            ApplySessionBalance(session);
                            SyncLiveBackgroundVpnState();
                            HandlePendingTileAction();
                            await HandlePendingBackgroundActionAsync();
                            await SyncBalanceFromServerAsync(session);
                        }
                        else
                        {
                            await SwitchToAppAfterAuthAsync(session);
                            SyncLiveBackgroundVpnState();
                            HandlePendingTileAction();
                            await HandlePendingBackgroundActionAsync();
                        }
#if ANDROID
                        if (OperatingSystem.IsAndroidVersionAtLeast(30))
                        {
                            _ = AndroidBillingRecoveryService.RecoverPendingPurchasesAsync(_apiService, TriggerBalanceSyncAsync);
                        }
#endif
                    }
                    else
                    {
                        if (_activeTab != "auth")
                        {
                            DesktopSidebar.HideSidebar();
                            MobileBottomBar.HideSidebar();
                            await SwitchTabAsync("auth", SplashOverlay.IsVisible);
                        }

                        if (SplashOverlay.IsVisible)
                        {
                            _ = _tabContentAuth?.PlayEntranceAnimationAsync();
                        }

                        await HandlePendingBackgroundActionAsync();

                        if (App.PendingDeepLinkLogin is not { } pendingLogin || _tabContentAuth is null)
                        {
                            return;
                        }

                        App.PendingDeepLinkLogin = null;
                        await _tabContentAuth.HandleDeepLinkLoginAsync(pendingLogin.Email, pendingLogin.Code);
                    }
                }
                catch
                {
                    // ignored
                }
            });
        });
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        SplashOverlay.InputTransparent = true;
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

    public async Task SwitchToAppAfterAuthAsync(UserSession? preloadedSession = null)
    {
        ApiService.ResetUnauthorizedState();
        var session = preloadedSession ?? await AuthManager.LoadSessionAsync();
        ApplySessionBalance(session);

        if (_tabContentAuth is { IsVisible: true })
        {
            _ = _tabContentAuth.FadeToAsync(0, 300);
            await Task.Delay(300);
        }

        var animateEntrance = _activeTab == "auth" || SplashOverlay.IsVisible;
        await SwitchTabAsync("vpn", animateEntrance);

        await SyncBalanceFromServerAsync(session);
    }

    //обработка отложенного нажатия системной плитки быстрого доступа
    public void HandlePendingTileAction()
    {
        if (!App.PendingTileAction)
        {
            return;
        }

        App.PendingTileAction = false;
        (EnsureTabCreated("vpn") as VpnView)?.TriggerTileToggle();
    }

    //синхронизация интерфейса и таймера часов при возврате в приложение, когда vpn уже работает в фоне
    private void SyncLiveBackgroundVpnState()
    {
        var liveState = VpnService.CurrentState;
        if (liveState == AppVpnState.Connected || OctopusEngine.Current.IsConnected)
        {
            NotifyVpnConnected();
        }

        _tabContentVpn?.SyncFromLiveVpnState();
    }

    //реакция на приход фонового действия из плитки шторки или голосового ассистента при уже открытом окне
    private void OnBackgroundActionReceived(BackgroundVpnActionKind _) =>
        MainThread.BeginInvokeOnMainThread(async void () =>
        {
            try
            {
                await HandlePendingBackgroundActionAsync();
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"[MAINPAGE BG ACTION ERROR] {ex.Message}");
            }
        });

    //показ целевого neo-диалога или запуск системного разрешения vpn по запросу из фона
    private async Task HandlePendingBackgroundActionAsync()
    {
        var action = App.PendingBackgroundAction;
        if (action == BackgroundVpnActionKind.None)
        {
            return;
        }

        App.PendingBackgroundAction = BackgroundVpnActionKind.None;

        switch (action)
        {
            case BackgroundVpnActionKind.NoInternet:
                await NeoAlert.ShowAsync(
                    "Нет подключения к сети",
                    "Отсутствует подключение к сети (Wi-Fi или мобильная сеть отключены).\n\nПожалуйста, проверьте доступ в интернет и повторите попытку.",
                    "Понятно");
                break;

            case BackgroundVpnActionKind.NoBalance:
                var openPayment = await NeoAlert.ShowConfirmAsync(
                    "Закончилось время",
                    "У вас закончилось доступное время баланса.\n\nПополните баланс, чтобы снова включить VPN.",
                    "Пополнить баланс",
                    "Отмена");
                if (openPayment && _activeTab != "auth")
                {
                    await SwitchTabAsync("payment");
                }

                break;

            case BackgroundVpnActionKind.RequireAuth:
                await NeoAlert.ShowAsync(
                    "Требуется вход",
                    "Войдите в аккаунт Obxodka, чтобы включать VPN из шторки быстрых настроек или голосовой командой.",
                    "Понятно");
                break;

            case BackgroundVpnActionKind.RequireVpnPermission:
                if (_activeTab != "auth")
                {
                    if (_activeTab != "vpn")
                    {
                        await SwitchTabAsync("vpn");
                    }

                    (EnsureTabCreated("vpn") as VpnView)?.TriggerTileToggle();
                }

                break;

            case BackgroundVpnActionKind.AssistantDisabled:
                var openSettings = await NeoAlert.ShowConfirmAsync(
                    "Голосовой помощник отключён",
                    "Управление VPN через Google Gemini и Samsung Bixby сейчас выключено в настройках приложения.\n\nОткрыть настройки Обходки?",
                    "Открыть настройки",
                    "Отмена");
                if (openSettings && _activeTab != "auth")
                {
                    await SwitchTabAsync("configuration");
                }

                break;
        }
    }

    private async Task HandleLogoutClickAsync()
    {
        var confirm = await DisplayAlertAsync("Выход", "Выйти из аккаунта?", "Да", "Отмена");
        if (confirm)
        {
            await PerformLogoutAsync();
        }
    }

    private int _authGeneration;

    private async Task PerformLogoutAsync()
    {
        _ = Interlocked.Increment(ref _authGeneration);

        if (_vpnCts is { } cts)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        _tabContentPayment?.StopPaymentPolling();
        _ = Task.Run(async () =>
        {
            try
            {
                await VpnService.StopVpnAsync();
            }
            catch
            {
                // ignored
            }
        });

        await AuthManager.ClearSessionAsync().ConfigureAwait(false);
        AppConfig.ApiBaseUrl = AppConfig.DefaultApiBaseUrl;

        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            RemainingSeconds = 0;
            _tabContentVpn?.UnsubscribeEvents();
            _tabContentVpn?.UpdateActiveNode();
            _tabContentProfile?.ResetAccountCache();
            _tabContentDevices?.InvalidateCache();
            DesktopSidebar.HideSidebar();
            MobileBottomBar.HideSidebar();
            await SwitchTabAsync("auth");
        });
    }
}
