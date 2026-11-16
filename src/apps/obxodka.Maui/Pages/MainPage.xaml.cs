namespace obxodka.Pages;

public sealed partial class MainPage : ContentPage, IDisposable
{
    public static MainPage? Current { get; private set; }

    private readonly ApiService _apiService;
    private readonly IAppManager _appManager;
    private readonly IAppUpdaterService? _appUpdaterService;
    private readonly ThemeManager _themeManager;
    private readonly IQrScannerService? _qrScanner;
    private CancellationTokenSource? _vpnCts;
    public long RemainingSeconds { get; private set; }
    private string _activeTab = "";
    private bool _isLoggingOut;
    private bool _isDecorationsFaded;
    private bool _isThemeVideoActive;
    private bool _isWindowActive = true;
    private bool _isVpnTabInitialized;
    private int _startupHandled;
    private int _isSyncingBalance;
    private long _lastEngineBytesSent;
    private long _lastEngineBytesReceived;
    private long _lastEngineTrafficTicks;
    private string? _activeThemeScreenFramePath;
    private FrameSlice? _activeThemeFrameSlice;
    private bool _hasThemeCornerDecorations;
    private List<string>? _activeParticleSpritePaths;
    private ThemeVfx? _activeVfx;
    private Color _activeParticlePrimary = Colors.Transparent;
    private Color _activeParticleAccent = Colors.Transparent;
    private ThemeAppliedEventArgs? _lastAppliedThemeArgs;

    private AuthView? _tabContentAuth;
    private VpnView? _tabContentVpn;
    private ConfigurationView? _tabContentConfiguration;
    private ProfileView? _tabContentProfile;
    private DevicesView? _tabContentDevices;
    private SplitView? _tabContentSplit;
    private DeleteAccountView? _tabContentDelete;
    private PaymentView? _tabContentPayment;
    private ThemeStoreView? _tabContentThemeStore;

    public IVpnService VpnService { get; }

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

        _themeManager.OnThemeApplied += HandleThemeApplied;
        _themeManager.OnThemeReset += HandleThemeReset;
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
                UpdateBalanceUI();
                DesktopSidebar.UpdateActiveTab("vpn");
                MobileBottomBar.UpdateActiveTab("vpn");
            }
            else
            {
                DesktopSidebar.HideSidebar();
                MobileBottomBar.HideSidebar();
            }
        }

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

        App.WindowActivated -= OnWindowActivated;
        App.WindowActivated += OnWindowActivated;
        App.WindowDeactivated -= OnWindowDeactivated;
        App.WindowDeactivated += OnWindowDeactivated;

        Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged;
        Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;

        DesktopSidebar.NavTapped += OnSidebarNavTapped;
        DesktopSidebar.LogoutTapped += OnSidebarLogoutTappedAsync;
        MobileBottomBar.NavTapped += OnBottomBarNavTapped;

        SafeAreaHelper.InsetsChanged -= OnSafeAreaInsetsChanged;
        SafeAreaHelper.InsetsChanged += OnSafeAreaInsetsChanged;
        if (SafeAreaHelper.TopInset > 0 || SafeAreaHelper.BottomInset > 0)
        {
            ApplySafeArea(SafeAreaHelper.TopInset, SafeAreaHelper.BottomInset);
        }

#if WINDOWS
        ThemeDecorationsContainer.HandlerChanged += (s, e) =>
        {
            if (ThemeDecorationsContainer.Handler?.PlatformView is Microsoft.UI.Xaml.UIElement el)
            {
                el.IsHitTestVisible = false;
            }
            ApplyWindowsClip(ThemeDecorationsContainer);
        };
        ThemeBackgroundViewport.HandlerChanged += (s, e) =>
        {
            if (ThemeBackgroundViewport.Handler?.PlatformView is Microsoft.UI.Xaml.UIElement el)
            {
                el.IsHitTestVisible = false;
            }
            ApplyWindowsClip(ThemeBackgroundViewport);
        };
        ThemeBackgroundMover.HandlerChanged += (s, e) =>
        {
            if (ThemeBackgroundMover.Handler?.PlatformView is Microsoft.UI.Xaml.UIElement el)
            {
                el.IsHitTestVisible = false;
            }
        };
        ThemeBackgroundImage.HandlerChanged += (s, e) =>
        {
            if (ThemeBackgroundImage.Handler?.PlatformView is Microsoft.UI.Xaml.UIElement el)
            {
                el.IsHitTestVisible = false;
            }
        };
        ThemeBackgroundVideo.HandlerChanged += (s, e) => ConfigureNativeVideoPlayer();
        ThemeBackgroundVideoDimmer.HandlerChanged += (s, e) =>
        {
            if (ThemeBackgroundVideoDimmer.Handler?.PlatformView is Microsoft.UI.Xaml.UIElement el)
            {
                el.IsHitTestVisible = false;
            }
        };
        ThisPage.HandlerChanged += (s, e) =>
        {
            ApplyWindowsClip(ThisPage);
            if (ThisPage.Handler?.PlatformView is Microsoft.UI.Xaml.UIElement el)
            {
                el.AddHandler(
                    Microsoft.UI.Xaml.UIElement.PointerMovedEvent,
                    new Microsoft.UI.Xaml.Input.PointerEventHandler((sender, args) =>
                    {
                        var pt = args.GetCurrentPoint(el);
                        HandlePointerInteraction(pt.Position.X, pt.Position.Y, ThisPage.Width, ThisPage.Height);
                    }),
                    true);

                el.AddHandler(
                    Microsoft.UI.Xaml.UIElement.PointerPressedEvent,
                    new Microsoft.UI.Xaml.Input.PointerEventHandler((sender, args) =>
                    {
                        var pt = args.GetCurrentPoint(el);
                        ThemeParticles?.TriggerPointerBurst((float)pt.Position.X, (float)pt.Position.Y);
                        if (!_isWindowActive)
                        {
                            OnWindowActivated();
                        }
                    }),
                    true);

                el.AddHandler(
                    Microsoft.UI.Xaml.UIElement.PointerExitedEvent,
                    new Microsoft.UI.Xaml.Input.PointerEventHandler((sender, args) =>
                    {
                        var pt = args.GetCurrentPoint(el);
                        var pos = pt.Position;
                        var w = ThisPage.Width;
                        var h = ThisPage.Height;
                        if (w <= 0 || h <= 0 || pos.X <= 4 || pos.Y <= 4 || pos.X >= w - 4 || pos.Y >= h - 4)
                        {
                            RestoreDecorationsOpacity();
                        }
                    }),
                    true);
            }
        };
#endif
    }

    public void Dispose()
    {
        _vpnCts?.Cancel();
        _vpnCts?.Dispose();
        _vpnCts = null;

        App.WindowActivated -= OnWindowActivated;
        App.WindowDeactivated -= OnWindowDeactivated;
        App.DeepLinkLoginReceived -= OnDeepLinkLoginReceived;

        _tabContentVpn?.UnsubscribeEvents();
        VpnService.OnForceLogoutRequested -= HandleForceLogout;
        VpnService.OnStateChanged -= OnVpnStateChangedForTheme;
        OctopusEngine.Current.OnTrafficUpdated -= OnEngineTrafficUpdated;
        ApiService.OnUnauthorized -= HandleApiUnauthorized;
        App.AppResumed -= OnAppResumed;
        Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged;

        _themeManager.OnThemeApplied -= HandleThemeApplied;
        _themeManager.OnThemeReset -= HandleThemeReset;
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
        _tabContentThemeStore?.BackRequested -= OnThemeStoreBackRequested;
        _tabContentVpn?.Dispose();
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
        MobileBottomBar.NavTapped -= OnBottomBarNavTapped;
        SafeAreaHelper.InsetsChanged -= OnSafeAreaInsetsChanged;
        if (Current == this)
        {
            Current = null;
        }

        GC.SuppressFinalize(this);
    }

    private void OnDeleteCancelRequested(object? sender, EventArgs e) =>
        _ = SwitchTabAsync("profile");

    private async void OnAccountDeletedAsync(object? sender, EventArgs e) =>
        await PerformLogoutAsync();

    private async void OnProfileLogoutRequestedAsync(object? sender, EventArgs e) =>
        await HandleLogoutClickAsync();

    private async void OnProfileBalanceRefreshRequestedAsync(object? sender, EventArgs e) =>
        await SyncBalanceFromServerAsync();

    public Task TriggerBalanceSyncAsync() => SyncBalanceFromServerAsync();

    private void OnConfigurationThemesRequested(object? sender, EventArgs e) =>
        _ = SwitchTabAsync("themes");

    private void OnThemeStoreBackRequested(object? sender, EventArgs e) =>
        _ = SwitchTabAsync("configuration");

    private static long ReadCachedRemainingSeconds()
    {
        var bal = PlatformServices.Preferences.Get(AppPreferenceKeys.UserBalanceSec, 0L);
        if (bal > 0)
        {
            return bal;
        }

        var subTicks = PlatformServices.Preferences.Get(AppPreferenceKeys.UserSubUntil, 0L);
        if (subTicks <= 0)
        {
            return 0;
        }

        var until = new DateTime(subTicks, DateTimeKind.Utc);
        return until > DateTime.UtcNow
            ? Math.Max(0, (long)(until - DateTime.UtcNow).TotalSeconds)
            : 0;
    }

    private async void OnBuyTokensRequestedAsync(object? sender, EventArgs e)
    {
        await SwitchTabAsync("payment");
        _tabContentPayment?.LoadPaymentPage();
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

    private void OnDeepLinkLoginReceived(string email, string code) =>
        MainThread.BeginInvokeOnMainThread(async () =>
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
        });

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

    private bool? _isWideLayout;

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var isDesktopOrTablet = DeviceInfo.Idiom == DeviceIdiom.Desktop || DeviceInfo.Idiom == DeviceIdiom.Tablet;
        var isWide = AdaptiveLayoutHelper.IsWideLayout(width, isDesktopOrTablet);
        if (_isWideLayout != isWide)
        {
            _isWideLayout = isWide;
            ApplyAdaptiveLayout(isWide);
        }

        if (!isWide)
        {
            MobileBottomBar.SetCompactMode(AdaptiveLayoutHelper.IsShortScreen(height));
        }
    }

    private void ApplyAdaptiveLayout(bool isWide)
    {
        if (isWide)
        {
            MobileBottomBar.IsVisible = false;
            MobileBottomBar.HideSidebar();
            DesktopSidebar.IsVisible = true;
            Grid.SetColumn(MainContentContainer, 1);
            Grid.SetColumnSpan(MainContentContainer, 1);
            if (!string.IsNullOrEmpty(_activeTab) && _activeTab != "auth")
            {
                _ = DesktopSidebar.PlayEntranceAnimationAsync();
            }
        }
        else
        {
            DesktopSidebar.IsVisible = false;
            DesktopSidebar.HideSidebar();
            MobileBottomBar.IsVisible = true;
            Grid.SetColumn(MainContentContainer, 0);
            Grid.SetColumnSpan(MainContentContainer, 2);
            if (!string.IsNullOrEmpty(_activeTab) && _activeTab != "auth")
            {
                _ = MobileBottomBar.PlayEntranceAnimationAsync();
            }
        }
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
                        await _appUpdaterService.CheckForUpdatesAsync(manualCheck: false);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"[AUTO UPDATE CHECK] {ex.Message}");
                }
            });

            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (SplashOverlay.IsVisible)
                {
                    await Task.Delay(1000);
                    _ = SplashOverlay.FadeToAsync(0, 350, Easing.CubicInOut);
                    await Task.Delay(360);
                    SplashOverlay.InputTransparent = true;
                    SplashOverlay.IsVisible = false;
#if WINDOWS
                    if (SplashOverlay.Handler?.PlatformView is Microsoft.UI.Xaml.UIElement splashElement)
                    {
                        splashElement.IsHitTestVisible = false;
                    }
#endif
                }

                if (session is { IsLoggedIn: true, JwtToken.Length: > 0 })
                {
                    if (_activeTab == "vpn")
                    {
                        ApplySessionBalance(session);
                        HandlePendingTileAction();
                        await SyncBalanceFromServerAsync(session);
                    }
                    else
                    {
                        await SwitchToAppAfterAuthAsync(session);
                        HandlePendingTileAction();
                    }
                }
                else
                {
                    if (_activeTab != "auth")
                    {
                        DesktopSidebar.HideSidebar();
                        MobileBottomBar.HideSidebar();
                        await SwitchTabAsync("auth", animate: SplashOverlay.IsVisible);
                    }
                    if (SplashOverlay.IsVisible)
                    {
                        _ = _tabContentAuth?.PlayEntranceAnimationAsync();
                    }
                    if (App.PendingDeepLinkLogin is { } pendingLogin && _tabContentAuth is not null)
                    {
                        App.PendingDeepLinkLogin = null;
                        await _tabContentAuth.HandleDeepLinkLoginAsync(pendingLogin.Email, pendingLogin.Code);
                    }
                }
            });
        });
    }

    private void ApplySessionBalance(UserSession? session)
    {
        if (session is null)
        {
            return;
        }

        RemainingSeconds = session.BalanceSeconds > 0
            ? session.BalanceSeconds
            : (session.SubscriptionUntil is { } until && until > DateTime.UtcNow
                ? Math.Max(0, (long)(until.ToUniversalTime() - DateTime.UtcNow).TotalSeconds)
                : 0);
        UpdateBalanceUI();
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
        var session = preloadedSession ?? await AuthManager.LoadSessionAsync();
        ApplySessionBalance(session);

        if (_tabContentAuth is { IsVisible: true })
        {
            _ = _tabContentAuth.FadeToAsync(0, 300);
            await Task.Delay(300);
        }

        var animateEntrance = _activeTab == "auth" || SplashOverlay.IsVisible;
        await SwitchTabAsync("vpn", animate: animateEntrance);

        var isDesktopOrTablet = DeviceInfo.Idiom == DeviceIdiom.Desktop || DeviceInfo.Idiom == DeviceIdiom.Tablet;
        var isWide = _isWideLayout ?? AdaptiveLayoutHelper.IsWideLayout(Width, isDesktopOrTablet);
        _isWideLayout = isWide;
        ApplyAdaptiveLayout(isWide);

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
        if (_vpnCts is { } cts)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }
        _tabContentVpn?.UnsubscribeEvents();
        _ = Task.Run(async () =>
        {
            try
            {
                await VpnService.StopVpnAsync();
            }
            catch { }
        });

        await AuthManager.ClearSessionAsync();
        AppConfig.ApiBaseUrl = AppConfig.DefaultApiBaseUrl;
        _tabContentVpn?.UpdateActiveNode();

        MainThread.BeginInvokeOnMainThread(() =>
        {
            DesktopSidebar.HideSidebar();
            MobileBottomBar.HideSidebar();
            _ = SwitchTabAsync("auth");
        });
    }

    private static readonly string[] s_mainMobileTabs = ["vpn", "profile", "devices", "split", "configuration"];

    private void OnContentSwipedLeft(object? sender, SwipedEventArgs e)
    {
        if (DeviceInfo.Idiom != DeviceIdiom.Phone || _activeTab == "auth")
        {
            return;
        }

        var idx = Array.IndexOf(s_mainMobileTabs, _activeTab);
        if (idx >= 0 && idx < s_mainMobileTabs.Length - 1)
        {
            _ = SwitchTabAsync(s_mainMobileTabs[idx + 1]);
        }
    }

    private void OnContentSwipedRight(object? sender, SwipedEventArgs e)
    {
        if (DeviceInfo.Idiom != DeviceIdiom.Phone || _activeTab == "auth")
        {
            return;
        }

        var idx = Array.IndexOf(s_mainMobileTabs, _activeTab);
        if (idx > 0)
        {
            _ = SwitchTabAsync(s_mainMobileTabs[idx - 1]);
        }
    }

    public async Task SwitchTabAsync(string tab, bool animate = true)
    {
        if (_activeTab == tab)
        {
            return;
        }

        var prevTab = _activeTab;
        if (prevTab == "payment")
        {
            _tabContentPayment?.StopPaymentPolling();
        }

        _activeTab = tab;

        DesktopSidebar.UpdateActiveTab(tab);
        MobileBottomBar.UpdateActiveTab(tab);

        var outgoing = GetTabContent(prevTab);
        var incoming = EnsureTabCreated(tab);
        await UIAnimations.SwitchViewAsync(outgoing, incoming, animate);
        UpdateAllViewsOpacity();

        switch (tab)
        {
            case "vpn":
                _tabContentVpn?.ForceLayoutWidth();
                if (animate)
                {
                    _ = _tabContentVpn?.PlayEntranceAnimationAsync();
                    if (!string.IsNullOrEmpty(prevTab))
                    {
                        _ = SyncBalanceFromServerAsync();
                    }
                }
                break;
            case "configuration":
                _tabContentConfiguration?.ForceLayoutWidth();
                _tabContentConfiguration?.OnAppearing();
                if (animate)
                {
                    _ = _tabContentConfiguration?.PlayEntranceAnimationAsync();
                }
                break;
            case "profile":
                _tabContentProfile?.ForceLayoutWidth();
                var session = await AuthManager.LoadSessionAsync();
                _tabContentProfile?.UpdateProfileInfo(session);
                _tabContentProfile?.UpdateBalance(RemainingSeconds);
                if (_tabContentProfile is not null)
                {
                    _ = _tabContentProfile.LoadReferralCodeAsync();
                    if (animate)
                    {
                        _ = _tabContentProfile.PlayEntranceAnimationAsync();
                    }
                }
                _ = SyncBalanceFromServerAsync(session);
                break;
            case "devices":
                _tabContentDevices?.ForceLayoutWidth();
                if (_tabContentDevices is not null)
                {
                    if (animate)
                    {
                        _ = _tabContentDevices.PlayEntranceAnimationAsync();
                    }
                    _ = _tabContentDevices.LoadDevicesAsync();
                }
                break;
            case "split":
                _tabContentSplit?.ForceLayoutWidth();
                if (_tabContentSplit is not null)
                {
                    if (animate)
                    {
                        _ = _tabContentSplit.PlayEntranceAnimationAsync();
                    }
                    _ = _tabContentSplit.LoadSplitAppsAsync();
                }
                break;
            case "auth":
                if (animate)
                {
                    _ = _tabContentAuth?.PlayEntranceAnimationAsync();
                }
                break;
            case "delete":
                if (animate)
                {
                    _ = _tabContentDelete?.PlayEntranceAnimationAsync();
                }
                break;
            case "payment":
                if (animate)
                {
                    _ = _tabContentPayment?.PlayEntranceAnimationAsync();
                }
                break;
            case "themes":
                _tabContentThemeStore?.ForceLayoutWidth();
                if (_tabContentThemeStore is not null)
                {
                    if (animate)
                    {
                        _ = _tabContentThemeStore.PlayEntranceAnimationAsync();
                    }
                    _ = _tabContentThemeStore.LoadCatalogAsync();
                }
                break;
            default:
                break;
        }
    }

    //ленивое создание и инициализация вкладки только при первом обращении
    private View EnsureTabCreated(string tab)
    {
        switch (tab)
        {
            case "vpn":
                if (_tabContentVpn is null)
                {
                    _tabContentVpn = new VpnView { IsVisible = false };
                    MainContentContainer.Children.Add(_tabContentVpn);
                }
                if (!_isVpnTabInitialized)
                {
                    _isVpnTabInitialized = true;
                    _tabContentVpn.Initialize(this, VpnService, _apiService);
                    ApplyInitialDecorations(_tabContentVpn);
                    if (_lastAppliedThemeArgs is { } themeArgs)
                    {
                        ApplyThemeToVpnTab(_tabContentVpn, themeArgs);
                        _tabContentVpn.SetCardFrameVisibility(_themeManager.FramesEnabled);
                    }
                    _tabContentVpn.UpdateBalanceUI(TimeFormatHelper.FormatSeconds(RemainingSeconds, false));
                }
                return _tabContentVpn;

            case "auth":
                if (_tabContentAuth is null)
                {
                    _tabContentAuth = new AuthView { IsVisible = false };
                    _tabContentAuth.Initialize(this, _apiService, _qrScanner);
                    ApplyInitialDecorations(_tabContentAuth);
                    MainContentContainer.Children.Add(_tabContentAuth);
                }
                return _tabContentAuth;

            case "configuration":
                if (_tabContentConfiguration is null)
                {
                    _tabContentConfiguration = new ConfigurationView { IsVisible = false };
                    _tabContentConfiguration.ThemesRequested += OnConfigurationThemesRequested;
                    ApplyInitialDecorations(_tabContentConfiguration);
                    MainContentContainer.Children.Add(_tabContentConfiguration);
                }
                return _tabContentConfiguration;

            case "profile":
                if (_tabContentProfile is null)
                {
                    _tabContentProfile = new ProfileView { IsVisible = false };
                    _tabContentProfile.Initialize(this, _themeManager, _apiService);
                    _tabContentProfile.LogoutRequested += OnProfileLogoutRequestedAsync;
                    _tabContentProfile.BuyTokensRequested += OnBuyTokensRequestedAsync;
                    _tabContentProfile.BalanceRefreshRequested += OnProfileBalanceRefreshRequestedAsync;
                    _tabContentProfile.UpdateBalance(RemainingSeconds);
                    ApplyInitialDecorations(_tabContentProfile);
                    MainContentContainer.Children.Add(_tabContentProfile);
                }
                return _tabContentProfile;

            case "devices":
                if (_tabContentDevices is null)
                {
                    _tabContentDevices = new DevicesView { IsVisible = false };
                    _tabContentDevices.Initialize(this, _apiService);
                    ApplyInitialDecorations(_tabContentDevices);
                    MainContentContainer.Children.Add(_tabContentDevices);
                }
                return _tabContentDevices;

            case "split":
                if (_tabContentSplit is null)
                {
                    _tabContentSplit = new SplitView { IsVisible = false };
                    _tabContentSplit.Initialize(this, _appManager);
                    ApplyInitialDecorations(_tabContentSplit);
                    MainContentContainer.Children.Add(_tabContentSplit);
                }
                return _tabContentSplit;

            case "delete":
                if (_tabContentDelete is null)
                {
                    _tabContentDelete = new DeleteAccountView { IsVisible = false };
                    _tabContentDelete.Initialize(this, _apiService);
                    _tabContentDelete.CancelRequested += OnDeleteCancelRequested;
                    _tabContentDelete.AccountDeleted += OnAccountDeletedAsync;
                    ApplyInitialDecorations(_tabContentDelete);
                    MainContentContainer.Children.Add(_tabContentDelete);
                }
                return _tabContentDelete;

            case "payment":
                if (_tabContentPayment is null)
                {
                    _tabContentPayment = new PaymentView { IsVisible = false };
                    _tabContentPayment.Initialize(this, _apiService);
                    _tabContentPayment.PaymentCompleted += OnPaymentCompletedAsync;
                    _tabContentPayment.PaymentCancelled += OnPaymentCancelled;
                    ApplyInitialDecorations(_tabContentPayment);
                    MainContentContainer.Children.Add(_tabContentPayment);
                }
                return _tabContentPayment;

            case "themes":
                if (_tabContentThemeStore is null)
                {
                    _tabContentThemeStore = new ThemeStoreView { IsVisible = false };
                    _tabContentThemeStore.Initialize(_themeManager);
                    _tabContentThemeStore.BackRequested += OnThemeStoreBackRequested;
                    ApplyInitialDecorations(_tabContentThemeStore);
                    MainContentContainer.Children.Add(_tabContentThemeStore);
                }
                return _tabContentThemeStore;

            default:
                return EnsureTabCreated("vpn");
        }
    }

    //применение актуальных отступов и темы на лениво созданную вкладку
    private void ApplyInitialDecorations(VisualElement view)
    {
        if (_lastAppliedTopInset > 0)
        {
            var safeTop = Math.Max(0, _lastAppliedTopInset);
            switch (view)
            {
                case VpnView v:
                    v.SetHeaderTopInset(safeTop);
                    break;
                case ConfigurationView c:
                    c.SetHeaderTopInset(safeTop);
                    break;
                case SplitView s:
                    s.SetHeaderTopInset(safeTop);
                    break;
                case ProfileView p:
                    p.SetHeaderTopInset(safeTop);
                    break;
                case ThemeStoreView ts:
                    ts.SetHeaderTopInset(safeTop);
                    break;
                case DevicesView d:
                    d.SetHeaderTopInset(safeTop);
                    break;
                case AuthView a:
                    a.SetHeaderTopInset(safeTop);
                    break;
                case PaymentView pm:
                    pm.SetHeaderTopInset(safeTop);
                    break;
                case DeleteAccountView da:
                    da.SetHeaderTopInset(safeTop);
                    break;
                default:
                    break;
            }
        }

        UpdateAllViewsOpacity();
    }

    private static void ApplyThemeToVpnTab(VpnView vpn, ThemeAppliedEventArgs e) =>
        vpn.ApplyThemeButton(
            e.ButtonImageIdlePath,
            e.ButtonImageConnectingPath,
            e.ButtonImageActivePath,
            e.ButtonImageErrorPath,
            e.ButtonVideoIdlePath,
            e.ButtonVideoConnectingPath,
            e.ButtonVideoActivePath,
            e.ButtonVideoErrorPath,
            e.CardFramePath,
            e.ButtonRingPath);

    private View? GetTabContent(string? tab) => tab switch
    {
        "auth" => _tabContentAuth,
        "vpn" => _tabContentVpn,
        "configuration" => _tabContentConfiguration,
        "profile" => _tabContentProfile,
        "devices" => _tabContentDevices,
        "payment" => _tabContentPayment,
        "split" => _tabContentSplit,
        "delete" => _tabContentDelete,
        "themes" => _tabContentThemeStore,
        _ => null
    };

    private void OnVpnStateChangedForTheme(AppVpnState state) =>
        MainThread.BeginInvokeOnMainThread(() => ThemeParticles?.TriggerVpnStateEffect(state));

    private void OnEngineTrafficUpdated(long bytesSent, long bytesReceived)
    {
        var nowTicks = Environment.TickCount64;
        var elapsedSec = (nowTicks - _lastEngineTrafficTicks) / 1000.0;
        if (elapsedSec <= 0.05)
        {
            return;
        }

        var deltaBytes = Math.Max(0L, bytesSent - _lastEngineBytesSent + (bytesReceived - _lastEngineBytesReceived));
        _lastEngineBytesSent = bytesSent;
        _lastEngineBytesReceived = bytesReceived;
        _lastEngineTrafficTicks = nowTicks;

        var totalMbps = deltaBytes * 8.0 / (elapsedSec * 1_000_000.0);
        ThemeParticles?.SetTrafficSpeedMbps(totalMbps);
    }

    public void NotifyVpnConnected()
    {
        DesktopSidebar.UpdateVpnStatus(true);
        if (_vpnCts is null && RemainingSeconds > 0)
        {
            _vpnCts = new CancellationTokenSource();
            _ = ConsumeTimeLoopAsync(_vpnCts.Token);
        }

        ThemeParticles?.TriggerVpnStateEffect(AppVpnState.Connected);

        if (_themeManager.SoundsEnabled)
        {
            ThemeAudioService.PlayConnect();
        }
    }

    public void NotifyVpnDisconnected()
    {
        DesktopSidebar.UpdateVpnStatus(false);
        _vpnCts?.Cancel();
        _vpnCts = null;

        ThemeParticles?.TriggerVpnStateEffect(AppVpnState.Disconnected);
        ThemeParticles?.SetTrafficSpeedMbps(0);
        ThemeAudioService.UpdateAmbientState(false);

        if (_themeManager.SoundsEnabled)
        {
            ThemeAudioService.PlayDisconnect();
        }
    }

    private static ImageSource? LoadLocalImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            return ImageSource.FromStream(() => new MemoryStream(bytes));
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[MainPage] Error loading local image '{path}': {ex.Message}");
            return null;
        }
    }

    private static void SetDecorationElement(Image target, string? path, ref bool hasAny)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            target.Source = LoadLocalImage(path);
            target.IsVisible = true;
            hasAny = true;
        }
        else
        {
            target.Source = null;
            target.IsVisible = false;
        }
    }

    private static void ClearImageElement(Image target)
    {
        target.Source = null;
        target.IsVisible = false;
    }

    private void OnCardOpacityChanged(double opacity) =>
        MainThread.BeginInvokeOnMainThread(UpdateAllViewsOpacity);

    private void OnGlowIntensityChanged(double glow) =>
        MainThread.BeginInvokeOnMainThread(() => _tabContentVpn?.OnThemeChanged());

    private void UpdateAllViewsOpacity()
    {
        _tabContentVpn?.UpdateCardOpacity();
        DesktopSidebar?.UpdateCardOpacity();
        MobileBottomBar?.UpdateCardOpacity();
        _tabContentConfiguration?.UpdateCardOpacity();
        _tabContentProfile?.UpdateCardOpacity();
        _tabContentThemeStore?.UpdateCardOpacity();
        _tabContentDevices?.UpdateCardOpacity();
        _tabContentPayment?.UpdateCardOpacity();
        _tabContentDelete?.UpdateCardOpacity();
        _tabContentSplit?.UpdateCardOpacity();
        _tabContentAuth?.UpdateCardOpacity();
    }

    private void NotifyViewsThemeChanged()
    {
        UpdateAllViewsOpacity();
        _tabContentVpn?.OnThemeChanged();
        _tabContentConfiguration?.OnThemeChanged();
        _tabContentSplit?.OnThemeChanged();
        DesktopSidebar?.OnThemeChanged();
        MobileBottomBar?.OnThemeChanged();
        _tabContentProfile?.OnThemeChanged();
        _tabContentThemeStore?.OnThemeChanged();
        _tabContentDevices?.OnThemeChanged();
        _tabContentPayment?.OnThemeChanged();
        _tabContentDelete?.OnThemeChanged();
        _tabContentAuth?.OnThemeChanged();
        NeoDialog?.OnThemeChanged();
    }

    private async Task ShowThemeTransitionAsync(string title, string? subtitle = null)
    {
        if (SplashOverlay.IsVisible)
        {
            return;
        }

        try
        {
            ThemeTransitionTitle.Text = title;
            ThemeTransitionSubtitle.Text = subtitle ?? "Настройка визуального оформления";
            ThemeTransitionOverlay.Opacity = 0;
            ThemeTransitionOverlay.IsVisible = true;
            ThemeTransitionSpinner.IsRunning = true;
            _ = await ThemeTransitionOverlay.FadeToAsync(1.0, 140, Easing.CubicOut);
        }
        catch
        {
        }
    }

    private async Task HideThemeTransitionAsync()
    {
        if (!ThemeTransitionOverlay.IsVisible)
        {
            return;
        }

        try
        {
            await Task.Delay(180);
            _ = await ThemeTransitionOverlay.FadeToAsync(0.0, 160, Easing.CubicIn);
            ThemeTransitionOverlay.IsVisible = false;
            ThemeTransitionSpinner.IsRunning = false;
        }
        catch
        {
            ThemeTransitionOverlay.IsVisible = false;
        }
    }

    private void HandleThemeApplied(ThemeAppliedEventArgs e)
    {
        _lastAppliedThemeArgs = e;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await ShowThemeTransitionAsync("Применение темы...", e.Manifest.Name);

                var bgBaseColor = Color.TryParse(e.Manifest.Core.Colors.BgBase, out var parsedBgBase)
                    ? parsedBgBase
                    : Color.FromArgb("#0E0E14");

                ThisPage.BackgroundColor = bgBaseColor;
                BackgroundColor = bgBaseColor;
#if ANDROID
                if (OperatingSystem.IsAndroidVersionAtLeast(29))
                {
                    MainActivity.UpdateSystemBarsTheme(bgBaseColor);
                }
#endif

                var willPlayVideo = !string.IsNullOrEmpty(e.BackgroundVideoPath) &&
                                    File.Exists(e.BackgroundVideoPath) &&
                                    _themeManager.VideoEnabled;

                ThemeBackgroundMover.CancelAnimations();
                ThemeBackgroundMover.Scale = _themeManager.ParallaxEnabled ? 1.015 : 1.0;
                ThemeBackgroundMover.TranslationX = 0;
                ThemeBackgroundMover.TranslationY = 0;

                if (!string.IsNullOrEmpty(e.BackgroundImagePath) && File.Exists(e.BackgroundImagePath))
                {
                    ThemeBackgroundImage.Source = LoadLocalImage(e.BackgroundImagePath);
                    ThemeBackgroundImage.Opacity = willPlayVideo ? 1.0 : (e.Manifest.Background?.Opacity ?? 0.45);
                    ThemeBackgroundImage.Scale = 1.0;
                    ThemeBackgroundImage.TranslationX = 0;
                    ThemeBackgroundImage.TranslationY = 0;
                    ThemeBackgroundImage.IsVisible = true;
                }
                else
                {
                    ClearImageElement(ThemeBackgroundImage);
                }

                var framesEnabled = _themeManager.FramesEnabled;
                _activeThemeScreenFramePath = e.ScreenFramePath;
                _activeThemeFrameSlice = e.FrameSlice;

                var hasScreenFrame = !string.IsNullOrEmpty(e.ScreenFramePath) && File.Exists(e.ScreenFramePath);
                if (framesEnabled && hasScreenFrame)
                {
                    ThemeScreenFrameOverlay.SetFrame(e.ScreenFramePath, e.FrameSlice);
                }
                else
                {
                    ThemeScreenFrameOverlay.Clear();
                }

                var hasTopDecorations = false;
                SetDecorationElement(ThemeCornerTopLeft, e.CornerTopLeftPath, ref hasTopDecorations);
                SetDecorationElement(ThemeCornerTopRight, e.CornerTopRightPath, ref hasTopDecorations);

                var hasBottomDecorations = false;
                SetDecorationElement(ThemeCornerBottomLeft, e.CornerBottomLeftPath, ref hasBottomDecorations);
                SetDecorationElement(ThemeCornerBottomRight, e.CornerBottomRightPath, ref hasBottomDecorations);

                var hasDecorations = hasTopDecorations || hasBottomDecorations;
                _hasThemeCornerDecorations = hasDecorations;
                ThemeScreenFrameOverlay.IsVisible = framesEnabled && hasScreenFrame;
                ThemeDecorationsContainer.IsVisible = framesEnabled && (hasScreenFrame || hasDecorations);
                ThemeDecorationsContainer.Opacity = 1.0;
                ThemeDecorationsContainer.TranslationX = 0;
                ThemeDecorationsContainer.TranslationY = 0;

                MainContentContainer.Margin = new Thickness(0);

                _ = Color.TryParse(e.Manifest.Core.Colors.Primary ?? string.Empty, out var primary);
                _ = Color.TryParse(e.Manifest.Core.Colors.Accent ?? string.Empty, out var accent);
                _activeParticleSpritePaths = e.ParticleSpritePaths;
                _activeVfx = e.Manifest.Vfx;
                _activeParticlePrimary = primary;
                _activeParticleAccent = accent;

                ThemeParticles.Configure(
                    e.ParticleSpritePaths,
                    e.Manifest.Vfx,
                    primary,
                    accent,
                    _themeManager.ParticlesEnabled,
                    e.ScreenOverlayPath);

                if (_tabContentVpn is not null)
                {
                    ApplyThemeToVpnTab(_tabContentVpn, e);
                }

                try
                {
                    ThemeBackgroundVideo.Stop();
                }
                catch { }

                if (willPlayVideo && e.BackgroundVideoPath is not null)
                {
                    try
                    {
                        _isThemeVideoActive = true;
                        ThemeBackgroundVideo.ShouldShowPlaybackControls = false;
                        ThemeBackgroundVideo.Opacity = 1.0;
                        ThemeBackgroundVideo.Scale = 1.0;
                        ThemeBackgroundVideo.TranslationX = 0;
                        ThemeBackgroundVideo.TranslationY = 0;
                        ThemeBackgroundVideo.Source = MediaSource.FromFile(e.BackgroundVideoPath);
                        ThemeBackgroundVideo.IsVisible = true;
                        if (_isWindowActive)
                        {
                            ThemeBackgroundVideo.Play();
                        }
                        else
                        {
                            ThemeBackgroundVideo.Pause();
                        }

                        var targetOpacity = e.Manifest.Background?.Opacity ?? 0.45;
                        ThemeBackgroundVideoDimmer.Color = bgBaseColor;
                        ThemeBackgroundVideoDimmer.Opacity = Math.Clamp(1.0 - targetOpacity, 0.0, 1.0);
                        ThemeBackgroundVideoDimmer.IsVisible = true;

#if WINDOWS
                        ConfigureNativeVideoPlayer();
#endif
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogWarning($"[MainPage] Video playback error: {ex.Message}");
                    }
                }
                else
                {
                    try
                    {
                        _isThemeVideoActive = false;
#if WINDOWS
#pragma warning disable CA1416
                        if (ThemeBackgroundVideo.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.MediaPlayerElement winPlayer)
                        {
                            try
                            {
                                winPlayer.MediaPlayer?.Pause();
                                winPlayer.SetMediaPlayer(null);
                            }
                            catch { }
                        }
#pragma warning restore CA1416
#endif
                        ThemeBackgroundVideo.IsVisible = false;
                        ThemeBackgroundVideo.Source = null;
                        ThemeBackgroundVideoDimmer.IsVisible = false;
                    }
                    catch { }
                }

                ThemeAudioService.UpdateAmbientState(false);

                NotifyViewsThemeChanged();
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"[MainPage] HandleThemeApplied error: {ex.Message}");
            }
            finally
            {
                await HideThemeTransitionAsync();
            }
        });
    }

    private void HandleThemeReset()
    {
        _lastAppliedThemeArgs = null;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await ShowThemeTransitionAsync("Сброс темы...", "Возвращение к стандартному оформлению");

                var defaultBg = (Color)(Application.Current?.Resources["BgBase"] ?? Color.FromArgb("#0E0E14"));
                ThisPage.BackgroundColor = defaultBg;
                BackgroundColor = defaultBg;
#if ANDROID
                if (OperatingSystem.IsAndroidVersionAtLeast(29))
                {
                    MainActivity.UpdateSystemBarsTheme(defaultBg);
                }
#endif

                ThemeBackgroundMover.CancelAnimations();
                ThemeBackgroundMover.Scale = 1.0;
                ThemeBackgroundMover.TranslationX = 0;
                ThemeBackgroundMover.TranslationY = 0;
                ClearImageElement(ThemeBackgroundImage);
                ThemeBackgroundImage.Scale = 1.0;
                ThemeBackgroundImage.TranslationX = 0;
                ThemeBackgroundImage.TranslationY = 0;
                ThemeScreenFrameOverlay.Clear();
                ClearImageElement(ThemeCornerTopLeft);
                ClearImageElement(ThemeCornerTopRight);
                ClearImageElement(ThemeCornerBottomLeft);
                ClearImageElement(ThemeCornerBottomRight);
                ThemeDecorationsContainer.IsVisible = false;
                ThemeDecorationsContainer.Opacity = 1.0;
                ThemeDecorationsContainer.TranslationX = 0;
                ThemeDecorationsContainer.TranslationY = 0;
                _isDecorationsFaded = false;
                MainContentContainer.Margin = new Thickness(0);

                ThemeParticles.Configure(spritePaths: null, null, null, null, false);
                _activeThemeScreenFramePath = null;
                _activeThemeFrameSlice = null;
                _hasThemeCornerDecorations = false;
                _activeParticleSpritePaths = null;
                _activeVfx = null;

#if WINDOWS
#pragma warning disable CA1416
                if (ThemeBackgroundVideo.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.MediaPlayerElement winPlayer)
                {
                    try
                    {
                        winPlayer.MediaPlayer?.Pause();
                        winPlayer.SetMediaPlayer(null);
                    }
                    catch { }
                }
#pragma warning restore CA1416
#endif
                try
                {
                    _isThemeVideoActive = false;
                    ThemeBackgroundVideo.Stop();
                }
                catch { }
                ThemeBackgroundVideo.IsVisible = false;
                ThemeBackgroundVideo.Source = null;
                ThemeBackgroundVideoDimmer.IsVisible = false;

                _tabContentVpn?.ResetThemeButton();
                NotifyViewsThemeChanged();
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"[MainPage] HandleThemeReset error: {ex.Message}");
            }
            finally
            {
                await HideThemeTransitionAsync();
            }
        });
    }

    private void HandlePointerInteraction(double x, double y, double width, double height)
    {
        ThemeParticles?.SetPointerPosition((float)x, (float)y, true);
        UpdateDecorationFade(x, y, width, height);
        UpdatePointerParallax(x, y, width, height);
    }

    private void UpdatePointerParallax(double x, double y, double width, double height)
    {
        if (width <= 1 || height <= 1)
        {
            return;
        }

        if (!_themeManager.ParallaxEnabled)
        {
            if (ThemeBackgroundMover.TranslationX != 0 || ThemeBackgroundMover.TranslationY != 0 || ThemeBackgroundMover.Scale != 1.0)
            {
                ThemeBackgroundMover.CancelAnimations();
                ThemeBackgroundMover.Scale = 1.0;
                ThemeBackgroundMover.TranslationX = 0;
                ThemeBackgroundMover.TranslationY = 0;
            }
            if (ThemeDecorationsContainer.TranslationX != 0 || ThemeDecorationsContainer.TranslationY != 0)
            {
                ThemeDecorationsContainer.TranslationX = 0;
                ThemeDecorationsContainer.TranslationY = 0;
            }
            return;
        }

        var rawParallax = _themeManager.ActiveTheme?.Vfx?.ParallaxIntensity ?? 0.35;
        var parallax = Math.Clamp(rawParallax <= 0.001 ? 0.35 : rawParallax, 0.0, 1.0);

        var nx = Math.Clamp(((x / width) - 0.5) * 2.0, -1.0, 1.0);
        var ny = Math.Clamp(((y / height) - 0.5) * 2.0, -1.0, 1.0);

        if (ThemeBackgroundImage.IsVisible || ThemeBackgroundVideo.IsVisible)
        {
            ThemeBackgroundMover.CancelAnimations();
            ThemeBackgroundMover.Scale = 1.015;
            var maxOffsetX = Math.Min(8.0 * parallax, 10.0);
            var maxOffsetY = Math.Min(3.0 * parallax, 4.0);
            ThemeBackgroundMover.TranslationX = -nx * maxOffsetX;
            ThemeBackgroundMover.TranslationY = -ny * maxOffsetY;
        }

        if (ThemeDecorationsContainer.IsVisible)
        {
            ThemeDecorationsContainer.TranslationX = nx * 3.0 * parallax;
            ThemeDecorationsContainer.TranslationY = ny * 2.0 * parallax;
        }
    }

    private void OnThemeParallaxEnabledChanged(bool enabled)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!enabled)
            {
                ThemeBackgroundMover.CancelAnimations();
                ThemeBackgroundMover.Scale = 1.0;
                ThemeBackgroundMover.TranslationX = 0;
                ThemeBackgroundMover.TranslationY = 0;
                ThemeDecorationsContainer.TranslationX = 0;
                ThemeDecorationsContainer.TranslationY = 0;
            }
            else
            {
                ThemeBackgroundMover.Scale = 1.015;
            }
        });
    }

    private void UpdateDecorationFade(double x, double y, double width, double height)
    {
        if (!ThemeDecorationsContainer.IsVisible || width <= 0 || height <= 0)
        {
            return;
        }

        const double topThreshold = 140;
        const double bottomThreshold = 80;
        const double sideThreshold = 100;

        var isNearEdgeOrCorner = y < topThreshold
                                || y > (height - bottomThreshold)
                                || x < sideThreshold
                                || x > (width - sideThreshold);

        if (isNearEdgeOrCorner)
        {
            if (!_isDecorationsFaded)
            {
                _isDecorationsFaded = true;
                ThemeDecorationsContainer.CancelAnimations();
                _ = ThemeDecorationsContainer.FadeToAsync(0.0, 120, Easing.CubicOut);
            }
        }
        else
        {
            if (_isDecorationsFaded)
            {
                _isDecorationsFaded = false;
                ThemeDecorationsContainer.CancelAnimations();
                _ = ThemeDecorationsContainer.FadeToAsync(1.0, 180, Easing.CubicIn);
            }
        }
    }

    private void RestoreDecorationsOpacity()
    {
        ThemeParticles?.SetPointerPosition(0, 0, false);

        if (ThemeBackgroundImage.IsVisible || ThemeBackgroundVideo.IsVisible)
        {
            ThemeBackgroundMover.CancelAnimations();
            _ = ThemeBackgroundMover.TranslateToAsync(0, 0, 220, Easing.CubicOut);
        }

        if (ThemeDecorationsContainer.IsVisible)
        {
            ThemeDecorationsContainer.TranslationX = 0;
            ThemeDecorationsContainer.TranslationY = 0;
            if (_isDecorationsFaded)
            {
                _isDecorationsFaded = false;
                ThemeDecorationsContainer.CancelAnimations();
                _ = ThemeDecorationsContainer.FadeToAsync(1.0, 180, Easing.CubicIn);
            }
        }
    }

    private void OnPagePointerMoved(object? sender, PointerEventArgs e)
    {
        var pos = e.GetPosition(ThisPage);
        if (pos.HasValue)
        {
            HandlePointerInteraction(pos.Value.X, pos.Value.Y, ThisPage.Width, ThisPage.Height);
        }
    }

    private void OnPagePointerPressed(object? sender, PointerEventArgs e)
    {
        var pos = e.GetPosition(ThisPage);
        if (pos.HasValue)
        {
            ThemeParticles?.TriggerPointerBurst((float)pos.Value.X, (float)pos.Value.Y);
        }
    }

    private void OnPageTapped(object? sender, TappedEventArgs e)
    {
        var pos = e.GetPosition(ThisPage);
        if (pos.HasValue)
        {
            ThemeParticles?.TriggerPointerBurst((float)pos.Value.X, (float)pos.Value.Y);
        }
    }

    public void TriggerParticleBurst(float x, float y) =>
        ThemeParticles?.TriggerPointerBurst(x, y);

    private void OnPagePointerExited(object? sender, PointerEventArgs e)
    {
#if WINDOWS
        var pos = e.GetPosition(ThisPage);
        if (pos.HasValue && ThisPage.Width > 0 && ThisPage.Height > 0)
        {
            var p = pos.Value;
            if (p.X > 4 && p.Y > 4 && p.X < ThisPage.Width - 4 && p.Y < ThisPage.Height - 4)
            {
                return;
            }
        }
#endif
        RestoreDecorationsOpacity();
    }

    private double _lastAppliedTopInset = -1;
    private double _lastAppliedBottomInset = -1;

    private void OnSafeAreaInsetsChanged(double top, double bottom) =>
        ApplySafeArea(top, bottom);

    private void ApplySafeArea(double top, double bottom)
    {
        if (DeviceInfo.Platform != DevicePlatform.Android && DeviceInfo.Platform != DevicePlatform.iOS)
        {
            return;
        }

        if (Math.Abs(_lastAppliedTopInset - top) < 0.5 && Math.Abs(_lastAppliedBottomInset - bottom) < 0.5)
        {
            return;
        }

        _lastAppliedTopInset = top;
        _lastAppliedBottomInset = bottom;

        MainContentContainer.Padding = new Thickness(0);
        MobileBottomBar.ApplyBottomInset(bottom);

        var safeTop = Math.Max(0, top);
        _tabContentVpn?.SetHeaderTopInset(safeTop);
        _tabContentConfiguration?.SetHeaderTopInset(safeTop);
        _tabContentSplit?.SetHeaderTopInset(safeTop);
        _tabContentProfile?.SetHeaderTopInset(safeTop);
        _tabContentThemeStore?.SetHeaderTopInset(safeTop);
        _tabContentDevices?.SetHeaderTopInset(safeTop);
        _tabContentAuth?.SetHeaderTopInset(safeTop);
        _tabContentPayment?.SetHeaderTopInset(safeTop);
        _tabContentDelete?.SetHeaderTopInset(safeTop);
    }

    private void UpdateBalanceUI()
    {
        var timeText = TimeFormatHelper.FormatSeconds(RemainingSeconds, false);
        _tabContentVpn?.UpdateBalanceUI(timeText);
        _tabContentProfile?.UpdateBalance(RemainingSeconds);
    }

    private void OnAppResumed() =>
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (_activeTab != "auth")
            {
                await SyncBalanceFromServerAsync();
            }
        });

    private void OnWindowActivated()
    {
        _isWindowActive = true;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_isThemeVideoActive && ThemeBackgroundVideo.IsVisible)
            {
                try
                {
                    ThemeBackgroundVideo.Play();
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"[MainPage] Error resuming video on activation: {ex.Message}");
                }
            }

            if (ThemeParticles.IsVisible)
            {
                try
                {
                    ThemeParticles.StartAnimation();
                }
                catch { }
            }

            _tabContentVpn?.OnWindowFocusChanged(true);
            if (!string.IsNullOrEmpty(_activeTab) && _activeTab != "auth")
            {
                _ = SyncBalanceFromServerAsync();
            }
        });
    }

    private void OnWindowDeactivated()
    {
        _isWindowActive = false;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var alwaysPlay = _themeManager?.AlwaysPlayVideoEnabled ?? false;
            if (!alwaysPlay)
            {
                if (_isThemeVideoActive && ThemeBackgroundVideo.IsVisible)
                {
                    try
                    {
                        ThemeBackgroundVideo.Pause();
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogWarning($"[MainPage] Error pausing video on deactivation: {ex.Message}");
                    }
                }

                if (ThemeParticles.IsVisible)
                {
                    try
                    {
                        ThemeParticles.StopAnimation();
                    }
                    catch { }
                }

                _tabContentVpn?.OnWindowFocusChanged(false);
            }
        });
    }

    private void OnThemeParticlesEnabledChanged(bool enabled) =>
        MainThread.BeginInvokeOnMainThread(() =>
            ThemeParticles.Configure(
                _activeParticleSpritePaths,
                _activeVfx,
                _activeParticlePrimary,
                _activeParticleAccent,
                enabled));

    private void OnThemeFramesEnabledChanged(bool enabled)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var hasFrame = !string.IsNullOrEmpty(_activeThemeScreenFramePath) && File.Exists(_activeThemeScreenFramePath);
            if (enabled && hasFrame)
            {
                ThemeScreenFrameOverlay.SetFrame(_activeThemeScreenFramePath, _activeThemeFrameSlice);
            }
            else
            {
                ThemeScreenFrameOverlay.Clear();
            }

            ThemeScreenFrameOverlay.IsVisible = enabled && hasFrame;
            ThemeDecorationsContainer.IsVisible = enabled && (hasFrame || _hasThemeCornerDecorations);
            ThemeParticles.InvalidateSurface();
            _tabContentVpn?.SetCardFrameVisibility(enabled);
        });
    }

    private void OnThemeVideoEnabledChanged(bool enabled)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!enabled)
            {
                _isThemeVideoActive = false;
                try
                {
                    ThemeBackgroundVideo.Stop();
                }
                catch { }
                ThemeBackgroundVideo.IsVisible = false;
                ThemeBackgroundVideo.Source = null;
                ThemeBackgroundVideoDimmer.IsVisible = false;
                if (ThemeBackgroundImage.IsVisible)
                {
                    ThemeBackgroundImage.Opacity = _themeManager.ActiveTheme?.Background?.Opacity ?? 0.45;
                }
            }
            else
            {
                if (_themeManager.ActiveTheme is not null)
                {
                    _themeManager.ReapplyActiveTheme();
                }
            }
        });
    }

    private void OnAlwaysPlayVideoChanged(bool alwaysPlay)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (alwaysPlay && !_isWindowActive)
            {
                if (_isThemeVideoActive && ThemeBackgroundVideo.IsVisible)
                {
                    try
                    {
                        ThemeBackgroundVideo.Play();
                    }
                    catch { }
                }

                if (ThemeParticles.IsVisible)
                {
                    try
                    {
                        ThemeParticles.StartAnimation();
                    }
                    catch { }
                }

                _tabContentVpn?.OnWindowFocusChanged(true);
            }
            else if (!alwaysPlay && !_isWindowActive)
            {
                if (_isThemeVideoActive && ThemeBackgroundVideo.IsVisible)
                {
                    try
                    {
                        ThemeBackgroundVideo.Pause();
                    }
                    catch { }
                }

                if (ThemeParticles.IsVisible)
                {
                    try
                    {
                        ThemeParticles.StopAnimation();
                    }
                    catch { }
                }

                _tabContentVpn?.OnWindowFocusChanged(false);
            }
        });
    }

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

    private async Task SyncBalanceFromServerAsync(UserSession? session = null, int retryCount = 0)
    {
        session ??= await AuthManager.LoadSessionAsync();
        if (session is null || string.IsNullOrEmpty(session.JwtToken))
        {
            return;
        }

        if (RemainingSeconds <= 0 && (session.BalanceSeconds > 0 || (session.SubscriptionUntil is { } initUntil && initUntil > DateTime.UtcNow)))
        {
            var initSec = session.BalanceSeconds > 0
                ? session.BalanceSeconds
                : (session.SubscriptionUntil is { } until
                    ? Math.Max(0, (long)(until.ToUniversalTime() - DateTime.UtcNow).TotalSeconds)
                    : 0);

            if (initSec > 0)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (RemainingSeconds <= 0)
                    {
                        RemainingSeconds = initSec;
                        UpdateBalanceUI();
                    }
                });
            }
        }

        if (Interlocked.CompareExchange(ref _isSyncingBalance, 1, 0) != 0)
        {
            return;
        }

        try
        {
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
                    _tabContentVpn?.UpdateBalanceUI(
                        TimeFormatHelper.FormatSeconds(RemainingSeconds, false),
                        VpnView.FormatBytes(profile.TotalBytesUsed));
                });
            }
            else
            {
                AppLogger.LogWarning($"[SYNC ERROR] {error}");
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    var fallbackSec = session.BalanceSeconds > 0
                        ? session.BalanceSeconds
                        : (session.SubscriptionUntil is { } until && until > DateTime.UtcNow
                            ? Math.Max(0, (long)(until.ToUniversalTime() - DateTime.UtcNow).TotalSeconds)
                            : 0);
                    if (fallbackSec > 0 && RemainingSeconds <= 0)
                    {
                        RemainingSeconds = fallbackSec;
                        UpdateBalanceUI();
                    }
                });

                if (retryCount < 3)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay((retryCount + 1) * 2500);
                        await SyncBalanceFromServerAsync(session, retryCount + 1);
                    });
                }
            }
        }
        finally
        {
            _ = Interlocked.Exchange(ref _isSyncingBalance, 0);
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
                PlatformThrottler.Post("ui:balance", TimeSpan.FromMilliseconds(250), UpdateBalanceUI);

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

#if WINDOWS
    private static void ApplyWindowsClip(VisualElement element)
    {
        if (element.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement fe)
        {
            UpdateFrameworkElementClip(fe);
            fe.SizeChanged -= OnNativeElementSizeChangedForClip;
            fe.SizeChanged += OnNativeElementSizeChangedForClip;
        }
    }

    private static void OnNativeElementSizeChangedForClip(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.FrameworkElement fe)
        {
            UpdateFrameworkElementClip(fe);
        }
    }

    private static void UpdateFrameworkElementClip(Microsoft.UI.Xaml.FrameworkElement fe)
    {
        var w = fe.ActualWidth;
        var h = fe.ActualHeight;
        if (w > 0 && h > 0)
        {
            fe.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, w, h)
            };
        }
    }

    private void ConfigureNativeVideoPlayer()
    {
#pragma warning disable CA1416
        try
        {
            if (ThemeBackgroundVideo.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.MediaPlayerElement winPlayer)
            {
                winPlayer.AreTransportControlsEnabled = false;
                winPlayer.IsHitTestVisible = false;
                if (winPlayer.TransportControls != null)
                {
                    winPlayer.TransportControls.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    winPlayer.TransportControls.IsEnabled = false;
                    winPlayer.TransportControls.IsHitTestVisible = false;
                }
                if (winPlayer.MediaPlayer != null)
                {
                    winPlayer.MediaPlayer.IsMuted = true;
                    winPlayer.MediaPlayer.IsLoopingEnabled = true;
                    winPlayer.MediaPlayer.AudioCategory = Windows.Media.Playback.MediaPlayerAudioCategory.Media;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[MainPage] Error configuring native video player: {ex.Message}");
        }
#pragma warning restore CA1416
    }
#endif

    #region Custom NeoCard Dialog

    //управление затемнением сайдбара при открытии модальных окон
    public async Task SetSidebarDimmedAsync(bool dimmed)
    {
        if (SidebarDimmer is null)
        {
            return;
        }

        if (dimmed)
        {
            SidebarDimmer.IsVisible = true;
            SidebarDimmer.InputTransparent = false;
            _ = await SidebarDimmer.FadeToAsync(1.0, 180, Easing.CubicOut);
        }
        else
        {
            SidebarDimmer.InputTransparent = true;
            _ = await SidebarDimmer.FadeToAsync(0.0, 150, Easing.CubicIn);
            SidebarDimmer.IsVisible = false;
        }
    }

    //клик по затемненному сайдбару для закрытия активной модалки
    private void OnSidebarDimmerTapped(object? sender, EventArgs e)
    {
        ThemeAudioService.PlayClick();
        if (_tabContentVpn?.IsServerModalOpen == true)
        {
            _ = _tabContentVpn.CloseServerModalAsync();
        }
        if (_tabContentThemeStore?.IsPreviewModalOpen == true)
        {
            _ = _tabContentThemeStore.ClosePreviewModalAsync();
        }
    }

    public Task<bool> ShowCustomDialogAsync(
        string title,
        string message,
        string? acceptText = null,
        string cancelText = "OK") =>
        NeoDialog.ShowAsync(title, message, acceptText, cancelText);

    public new Task<bool> DisplayAlert(string title, string message, string accept, string cancel) =>
        ShowCustomDialogAsync(title, message, accept, cancel);

    public new Task DisplayAlert(string title, string message, string cancel) =>
        ShowCustomDialogAsync(title, message, null, cancel);

    public new Task<bool> DisplayAlert(string title, string message, string accept, string cancel, FlowDirection flowDirection)
    {
        _ = flowDirection;
        return ShowCustomDialogAsync(title, message, accept, cancel);
    }

    public new Task DisplayAlert(string title, string message, string cancel, FlowDirection flowDirection)
    {
        _ = flowDirection;
        return ShowCustomDialogAsync(title, message, null, cancel);
    }

    public new Task<bool> DisplayAlertAsync(string title, string message, string accept, string cancel, FlowDirection flowDirection = FlowDirection.MatchParent)
    {
        _ = flowDirection;
        return ShowCustomDialogAsync(title, message, accept, cancel);
    }

    public new Task DisplayAlertAsync(string title, string message, string cancel, FlowDirection flowDirection = FlowDirection.MatchParent)
    {
        _ = flowDirection;
        return ShowCustomDialogAsync(title, message, null, cancel);
    }

    #endregion
}
