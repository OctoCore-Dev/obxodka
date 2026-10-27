namespace obxodka.Pages;

public sealed partial class MainPage : ContentPage, IDisposable
{
    public static MainPage? Current { get; private set; }

    private readonly ApiService _apiService;
    private readonly IAppManager _appManager;
    private readonly IAppUpdaterService? _appUpdaterService;
    private readonly ThemeManager _themeManager;
    private CancellationTokenSource? _vpnCts;
    public long RemainingSeconds { get; private set; }
    private string _activeTab = "";
    private bool _isLoggingOut;
    private bool _isDecorationsFaded;
    private bool _isThemeVideoActive;
    private bool _isWindowActive = true;
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

    public IVpnService VpnService { get; }

    public MainPage(
        IVpnService vpnService,
        ApiService apiService,
        IAppManager appManager,
        ThemeManager themeManager,
        IAppUpdaterService? appUpdaterService = null)
    {
        Current = this;
        InitializeComponent();
        if (SeasonSchedule.IsHalloweenSeason())
        {
            SplashIcon.Source = "splash_logo_halloween.png";
        }
        VpnService = vpnService;
        _apiService = apiService;
        _appManager = appManager;
        _themeManager = themeManager;
        _appUpdaterService = appUpdaterService;
        BindingContext = this;

        TabContentAuth.Initialize(this, _apiService);

        TabContentDelete.Initialize(this, _apiService);
        TabContentDelete.CancelRequested += OnDeleteCancelRequested;
        TabContentDelete.AccountDeleted += OnAccountDeletedAsync;

        TabContentDevices.Initialize(this, _apiService);
        TabContentSplit.Initialize(this, _appManager);
        TabContentPayment.Initialize(this, _apiService);
        TabContentProfile.Initialize(this, _themeManager, _apiService);

        TabContentThemeStore.Initialize(_themeManager);
        TabContentThemeStore.BackRequested += (_, _) => _ = SwitchTabAsync("configuration");
        TabContentConfiguration.ThemesRequested += (_, _) => _ = SwitchTabAsync("themes");

        _themeManager.OnThemeApplied += HandleThemeApplied;
        _themeManager.OnThemeReset += HandleThemeReset;
        _themeManager.VideoEnabledChanged += OnThemeVideoEnabledChanged;
        _themeManager.ParticlesEnabledChanged += OnThemeParticlesEnabledChanged;
        _themeManager.FramesEnabledChanged += OnThemeFramesEnabledChanged;
        _themeManager.AlwaysPlayVideoChanged += OnAlwaysPlayVideoChanged;
        _themeManager.ParallaxEnabledChanged += OnThemeParallaxEnabledChanged;
        _themeManager.CardOpacityChanged += OnCardOpacityChanged;
        _themeManager.GlowIntensityChanged += OnGlowIntensityChanged;
        _themeManager.RestoreActiveThemeAtStartup();
        UpdateAllViewsOpacity();

        TabContentProfile.LogoutRequested += OnProfileLogoutRequestedAsync;
        TabContentProfile.BuyTokensRequested += OnBuyTokensRequested;
        TabContentProfile.BalanceRefreshRequested += OnProfileBalanceRefreshRequestedAsync;

        TabContentPayment.PaymentCompleted += OnPaymentCompletedAsync;
        TabContentPayment.PaymentCancelled += OnPaymentCancelled;

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

        TabContentVpn.UnsubscribeEvents();
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

        TabContentDelete.CancelRequested -= OnDeleteCancelRequested;
        TabContentDelete.AccountDeleted -= OnAccountDeletedAsync;
        TabContentProfile.LogoutRequested -= OnProfileLogoutRequestedAsync;
        TabContentProfile.BuyTokensRequested -= OnBuyTokensRequested;
        TabContentProfile.BalanceRefreshRequested -= OnProfileBalanceRefreshRequestedAsync;
        TabContentPayment.PaymentCompleted -= OnPaymentCompletedAsync;
        TabContentPayment.PaymentCancelled -= OnPaymentCancelled;
        TabContentPayment.StopPaymentPolling();
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

            await TabContentAuth.HandleDeepLinkLoginAsync(email, code);
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
        if (SeasonSchedule.IsHalloweenSeason())
        {
            SplashIcon.Source = "splash_logo_halloween.png";
        }
        _ = AnimateSplashIconAsync();

        _ = Task.Run(async () =>
        {
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
                    await SwitchTabAsync("auth");
                    _ = TabContentAuth.PlayEntranceAnimationAsync();
                    if (App.PendingDeepLinkLogin is { } pendingLogin)
                    {
                        App.PendingDeepLinkLogin = null;
                        await TabContentAuth.HandleDeepLinkLoginAsync(pendingLogin.Email, pendingLogin.Code);
                    }
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

    public async Task SwitchToAppAfterAuthAsync()
    {
        var session = await AuthManager.LoadSessionAsync();
        if (session is not null)
        {
            RemainingSeconds = session.BalanceSeconds > 0
                ? session.BalanceSeconds
                : (session.SubscriptionUntil is { } until && until > DateTime.UtcNow
                    ? Math.Max(0, (long)(until.ToUniversalTime() - DateTime.UtcNow).TotalSeconds)
                    : 0);
            UpdateBalanceUI();
        }

        _ = TabContentAuth.FadeToAsync(0, 300);
        await Task.Delay(300);

        TabContentVpn.Initialize(this, VpnService, _apiService);
        _ = SwitchTabAsync("vpn");

        var isDesktopOrTablet = DeviceInfo.Idiom == DeviceIdiom.Desktop || DeviceInfo.Idiom == DeviceIdiom.Tablet;
        var isWide = _isWideLayout ?? AdaptiveLayoutHelper.IsWideLayout(Width, isDesktopOrTablet);
        _isWideLayout = isWide;
        ApplyAdaptiveLayout(isWide);

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
        AppConfig.ApiBaseUrl = AppConfig.DefaultApiBaseUrl;
        TabContentVpn.UpdateActiveNode();

        MainThread.BeginInvokeOnMainThread(() =>
        {
            DesktopSidebar.HideSidebar();
            MobileBottomBar.HideSidebar();
            _ = SwitchTabAsync("auth");
        });
    }

    private static readonly string[] t_mainMobileTabs = ["vpn", "profile", "devices", "split", "configuration"];

    private void OnContentSwipedLeft(object? sender, SwipedEventArgs e)
    {
        if (DeviceInfo.Idiom != DeviceIdiom.Phone || _activeTab == "auth")
        {
            return;
        }

        var idx = Array.IndexOf(t_mainMobileTabs, _activeTab);
        if (idx >= 0 && idx < t_mainMobileTabs.Length - 1)
        {
            _ = SwitchTabAsync(t_mainMobileTabs[idx + 1]);
        }
    }

    private void OnContentSwipedRight(object? sender, SwipedEventArgs e)
    {
        if (DeviceInfo.Idiom != DeviceIdiom.Phone || _activeTab == "auth")
        {
            return;
        }

        var idx = Array.IndexOf(t_mainMobileTabs, _activeTab);
        if (idx > 0)
        {
            _ = SwitchTabAsync(t_mainMobileTabs[idx - 1]);
        }
    }

    public async Task SwitchTabAsync(string tab)
    {
        if (_activeTab == tab)
        {
            return;
        }

        var prevTab = _activeTab;
        if (prevTab == "payment")
        {
            TabContentPayment.StopPaymentPolling();
        }

        _activeTab = tab;

        DesktopSidebar.UpdateActiveTab(tab);
        MobileBottomBar.UpdateActiveTab(tab);

        var outgoing = GetTabContent(prevTab);
        var incoming = GetTabContent(tab);
        await UIAnimations.SwitchViewAsync(outgoing, incoming);
        UpdateAllViewsOpacity();

        switch (tab)
        {
            case "vpn":
                TabContentVpn.ForceLayoutWidth();
                _ = TabContentVpn.PlayEntranceAnimationAsync();
                _ = SyncBalanceFromServerAsync();
                break;
            case "configuration":
                TabContentConfiguration.ForceLayoutWidth();
                TabContentConfiguration.OnAppearing();
                _ = TabContentConfiguration.PlayEntranceAnimationAsync();
                break;
            case "profile":
                TabContentProfile.ForceLayoutWidth();
                var session = await AuthManager.LoadSessionAsync();
                TabContentProfile.UpdateProfileInfo(session);
                TabContentProfile.UpdateBalance(RemainingSeconds);
                _ = TabContentProfile.LoadReferralCodeAsync();
                _ = TabContentProfile.PlayEntranceAnimationAsync();
                _ = SyncBalanceFromServerAsync(session);
                break;
            case "devices":
                TabContentDevices.ForceLayoutWidth();
                _ = TabContentDevices.PlayEntranceAnimationAsync();
                _ = TabContentDevices.LoadDevicesAsync();
                break;
            case "split":
                TabContentSplit.ForceLayoutWidth();
                _ = TabContentSplit.PlayEntranceAnimationAsync();
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
            case "themes":
                TabContentThemeStore.ForceLayoutWidth();
                _ = TabContentThemeStore.PlayEntranceAnimationAsync();
                _ = TabContentThemeStore.LoadCatalogAsync();
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
        "devices" => TabContentDevices,
        "payment" => TabContentPayment,
        "split" => TabContentSplit,
        "delete" => TabContentDelete,
        "themes" => TabContentThemeStore,
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
            Debug.WriteLine($"[MainPage] Error loading local image '{path}': {ex.Message}");
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
        MainThread.BeginInvokeOnMainThread(() => TabContentVpn?.OnThemeChanged());

    private void UpdateAllViewsOpacity()
    {
        TabContentVpn?.UpdateCardOpacity();
        DesktopSidebar?.UpdateCardOpacity();
        MobileBottomBar?.UpdateCardOpacity();
        TabContentConfiguration?.UpdateCardOpacity();
        TabContentProfile?.UpdateCardOpacity();
        TabContentThemeStore?.UpdateCardOpacity();
        TabContentDevices?.UpdateCardOpacity();
        TabContentPayment?.UpdateCardOpacity();
        TabContentDelete?.UpdateCardOpacity();
        TabContentSplit?.UpdateCardOpacity();
        TabContentAuth?.UpdateCardOpacity();
    }

    private void NotifyViewsThemeChanged()
    {
        UpdateAllViewsOpacity();
        TabContentVpn?.OnThemeChanged();
        TabContentConfiguration?.OnThemeChanged();
        TabContentSplit?.OnThemeChanged();
        DesktopSidebar?.OnThemeChanged();
        MobileBottomBar?.OnThemeChanged();
        TabContentProfile?.OnThemeChanged();
        TabContentThemeStore?.OnThemeChanged();
        TabContentDevices?.OnThemeChanged();
        TabContentPayment?.OnThemeChanged();
        TabContentDelete?.OnThemeChanged();
        TabContentAuth?.OnThemeChanged();
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

                _ = Color.TryParse(e.Manifest.Core.Colors.Primary, out var primary);
                _ = Color.TryParse(e.Manifest.Core.Colors.Accent, out var accent);
                _activeParticleSpritePaths = e.ParticleSpritePaths;
                _activeVfx = e.Manifest.Vfx;
                _activeParticlePrimary = primary;
                _activeParticleAccent = accent;

                ThemeParticles.Configure(
                    e.ParticleSpritePaths,
                    e.Manifest.Vfx,
                    primary,
                    accent,
                    _themeManager.ParticlesEnabled);

                TabContentVpn?.ApplyThemeButton(
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
                        Debug.WriteLine($"[MainPage] Video playback error: {ex.Message}");
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
                Debug.WriteLine($"[MainPage] HandleThemeApplied error: {ex.Message}");
            }
            finally
            {
                await HideThemeTransitionAsync();
            }
        });
    }

    private void HandleThemeReset()
    {
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

                TabContentVpn?.ResetThemeButton();
                NotifyViewsThemeChanged();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainPage] HandleThemeReset error: {ex.Message}");
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

    private void OnSafeAreaInsetsChanged(double top, double bottom) =>
        MainThread.BeginInvokeOnMainThread(() => ApplySafeArea(top, bottom));

    private void ApplySafeArea(double top, double bottom)
    {
        if (DeviceInfo.Platform != DevicePlatform.Android && DeviceInfo.Platform != DevicePlatform.iOS)
        {
            return;
        }

        MainContentContainer.Padding = new Thickness(0);
        MobileBottomBar.Margin = new Thickness(16, 0, 16, Math.Max(16, bottom + 8));

        var safeTop = Math.Max(0, top);
        TabContentVpn?.SetHeaderTopInset(safeTop);
        TabContentConfiguration?.SetHeaderTopInset(safeTop);
        TabContentSplit?.SetHeaderTopInset(safeTop);
        TabContentProfile?.SetHeaderTopInset(safeTop);
        TabContentThemeStore?.SetHeaderTopInset(safeTop);
        TabContentDevices?.SetHeaderTopInset(safeTop);
        TabContentAuth?.SetHeaderTopInset(safeTop);
        TabContentPayment?.SetHeaderTopInset(safeTop);
        TabContentDelete?.SetHeaderTopInset(safeTop);
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
                    Debug.WriteLine($"[MainPage] Error resuming video on activation: {ex.Message}");
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

            TabContentVpn?.OnWindowFocusChanged(true);
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
                        Debug.WriteLine($"[MainPage] Error pausing video on deactivation: {ex.Message}");
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

                TabContentVpn?.OnWindowFocusChanged(false);
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
            TabContentVpn?.SetCardFrameVisibility(enabled);
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

                TabContentVpn?.OnWindowFocusChanged(true);
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

                TabContentVpn?.OnWindowFocusChanged(false);
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
                    TabContentVpn.UpdateBalanceUI(
                        TimeFormatHelper.FormatSeconds(RemainingSeconds, false),
                        VpnView.FormatBytes(profile.TotalBytesUsed));
                });
            }
            else
            {
                Debug.WriteLine($"[SYNC ERROR] {error}");
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
            Debug.WriteLine($"[MainPage] Error configuring native video player: {ex.Message}");
        }
#pragma warning restore CA1416
    }
#endif

    #region Custom NeoCard Dialog

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
