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
        TabContentProfile.Initialize(this, _themeManager);
        TabContentFriends.Initialize(_apiService);
        TabContentFriends.BackRequested += (_, _) => _ = SwitchTabAsync("profile");
        TabContentMesh.Initialize(_apiService);

        TabContentThemeStore.Initialize(_themeManager);
        TabContentThemeStore.BackRequested += (_, _) => _ = SwitchTabAsync("configuration");
        TabContentConfiguration.ThemesRequested += (_, _) => _ = SwitchTabAsync("themes");

        TabContentVpn.Initialize(this, VpnService, _apiService);

        _themeManager.OnThemeApplied += HandleThemeApplied;
        _themeManager.OnThemeReset += HandleThemeReset;
        _themeManager.VideoEnabledChanged += OnThemeVideoEnabledChanged;
        _themeManager.AlwaysPlayVideoChanged += OnAlwaysPlayVideoChanged;
        _themeManager.CardOpacityChanged += OnCardOpacityChanged;
        _themeManager.RestoreActiveThemeAtStartup();
        UpdateAllViewsOpacity();

        TabContentProfile.LogoutRequested += OnProfileLogoutRequestedAsync;
        TabContentProfile.BuyTokensRequested += OnBuyTokensRequested;
        TabContentProfile.FriendsRequested += (_, _) => _ = SwitchTabAsync("friends");

        TabContentPayment.PaymentCompleted += OnPaymentCompletedAsync;
        TabContentPayment.PaymentCancelled += OnPaymentCancelled;

        VpnService.OnForceLogoutRequested -= HandleForceLogout;
        VpnService.OnForceLogoutRequested += HandleForceLogout;

        ApiService.OnUnauthorized -= HandleApiUnauthorized;
        ApiService.OnUnauthorized += HandleApiUnauthorized;

        App.AppResumed -= OnAppResumed;
        App.AppResumed += OnAppResumed;

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
            if (ThisPage.Handler?.PlatformView is Microsoft.UI.Xaml.UIElement el)
            {
                el.AddHandler(
                    Microsoft.UI.Xaml.UIElement.PointerMovedEvent,
                    new Microsoft.UI.Xaml.Input.PointerEventHandler((sender, args) =>
                    {
                        var pt = args.GetCurrentPoint(el);
                        UpdateDecorationFade(pt.Position.X, pt.Position.Y, ThisPage.Width, ThisPage.Height);
                    }),
                    true);

                el.AddHandler(
                    Microsoft.UI.Xaml.UIElement.PointerPressedEvent,
                    new Microsoft.UI.Xaml.Input.PointerEventHandler((sender, args) =>
                    {
                        if (!_isWindowActive)
                        {
                            OnWindowActivated();
                        }
                    }),
                    true);

                el.AddHandler(
                    Microsoft.UI.Xaml.UIElement.PointerExitedEvent,
                    new Microsoft.UI.Xaml.Input.PointerEventHandler((sender, args) => RestoreDecorationsOpacity()),
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

        TabContentVpn.UnsubscribeEvents();
        VpnService.OnForceLogoutRequested -= HandleForceLogout;
        ApiService.OnUnauthorized -= HandleApiUnauthorized;
        App.AppResumed -= OnAppResumed;
        Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged;

        _themeManager.OnThemeApplied -= HandleThemeApplied;
        _themeManager.OnThemeReset -= HandleThemeReset;
        _themeManager.VideoEnabledChanged -= OnThemeVideoEnabledChanged;
        _themeManager.AlwaysPlayVideoChanged -= OnAlwaysPlayVideoChanged;
        _themeManager.CardOpacityChanged -= OnCardOpacityChanged;

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
        _ = AnimateSplashIconAsync();

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(7));
                var activeHost = await DiscoveryService.GetActiveBridgeUrlAsync(forceRefresh: true, ct: cts.Token);
                if (!string.IsNullOrEmpty(activeHost))
                {
                    AppConfig.ApiBaseUrl = $"https://{activeHost}/";
                    TabContentVpn.UpdateActiveNode(activeHost);
                    _ = SyncBalanceFromServerAsync();
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
                    _ = SwitchTabAsync("auth");
                    _ = TabContentAuth.PlayEntranceAnimationAsync();
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

    private static readonly string[] t_mainMobileTabs = ["vpn", "profile", "devices", "configuration", "friends"];

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
                _ = TabContentProfile.PlayEntranceAnimationAsync();
                _ = SyncBalanceFromServerAsync(session);
                break;
            case "friends":
                TabContentFriends.ForceLayoutWidth();
                _ = TabContentFriends.OnAppearingAsync();
                _ = TabContentFriends.PlayEntranceAnimationAsync();
                break;
            case "mesh":
                TabContentMesh.Initialize(_apiService);
                TabContentMesh.ForceLayoutWidth();
                _ = TabContentMesh.PlayEntranceAnimationAsync();
                break;
            case "devices":
                TabContentDevices.ForceLayoutWidth();
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
        "friends" => TabContentFriends,
        "mesh" => TabContentMesh,
        "devices" => TabContentDevices,
        "payment" => TabContentPayment,
        "split" => TabContentSplit,
        "delete" => TabContentDelete,
        "themes" => TabContentThemeStore,
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

        if (_themeManager.SoundsEnabled)
        {
            var connectSound = _themeManager.ActiveTheme?.Core.Sounds?.Connect;
            if (string.IsNullOrEmpty(connectSound) && _themeManager.ActiveTheme?.Apps?.ContainsKey("obxodka") == true)
            {
                try
                {
                    connectSound = _themeManager.ActiveTheme.Apps["obxodka"].GetProperty("sounds").GetProperty("connect").GetString();
                }
                catch
                {
                }
            }
            if (!string.IsNullOrEmpty(connectSound) && _themeManager.ActiveThemeFolderPath != null)
            {
                var soundPath = Path.Combine(_themeManager.ActiveThemeFolderPath, connectSound);
                ThemeAudioService.PlaySound(soundPath, true);
            }
        }
    }

    public void NotifyVpnDisconnected()
    {
        DesktopSidebar.UpdateVpnStatus(false);
        _vpnCts?.Cancel();
        _vpnCts = null;

        if (_themeManager.SoundsEnabled)
        {
            var disconnectSound = _themeManager.ActiveTheme?.Core.Sounds?.Disconnect;
            if (string.IsNullOrEmpty(disconnectSound) && _themeManager.ActiveTheme?.Apps?.ContainsKey("obxodka") == true)
            {
                try
                {
                    disconnectSound = _themeManager.ActiveTheme.Apps["obxodka"].GetProperty("sounds").GetProperty("disconnect").GetString();
                }
                catch
                {
                }
            }
            if (!string.IsNullOrEmpty(disconnectSound) && _themeManager.ActiveThemeFolderPath != null)
            {
                var soundPath = Path.Combine(_themeManager.ActiveThemeFolderPath, disconnectSound);
                ThemeAudioService.PlaySound(soundPath, true);
            }
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

    private void UpdateAllViewsOpacity()
    {
        var bgSurface = (Application.Current?.Resources.TryGetValue("BgSurface", out var bg) == true && bg is Color bgColor)
            ? bgColor
            : Color.FromArgb("#161622");

        ThemeManager.ApplyVisualTreeCardOpacity(TabContentVpn, bgSurface);
        ThemeManager.ApplyVisualTreeCardOpacity(DesktopSidebar, bgSurface);
        ThemeManager.ApplyVisualTreeCardOpacity(MobileBottomBar, bgSurface);
        ThemeManager.ApplyVisualTreeCardOpacity(TabContentConfiguration, bgSurface);
        ThemeManager.ApplyVisualTreeCardOpacity(TabContentProfile, bgSurface);
        ThemeManager.ApplyVisualTreeCardOpacity(TabContentThemeStore, bgSurface);
        ThemeManager.ApplyVisualTreeCardOpacity(TabContentMesh, bgSurface);
        ThemeManager.ApplyVisualTreeCardOpacity(TabContentDevices, bgSurface);
        ThemeManager.ApplyVisualTreeCardOpacity(TabContentFriends, bgSurface);
        ThemeManager.ApplyVisualTreeCardOpacity(TabContentPayment, bgSurface);
        ThemeManager.ApplyVisualTreeCardOpacity(TabContentDelete, bgSurface);
        ThemeManager.ApplyVisualTreeCardOpacity(TabContentSplit, bgSurface);
        ThemeManager.ApplyVisualTreeCardOpacity(TabContentAuth, bgSurface);

        TabContentVpn?.UpdateCardOpacity();
        DesktopSidebar?.UpdateCardOpacity();
        MobileBottomBar?.UpdateCardOpacity();
        TabContentConfiguration?.UpdateCardOpacity();
        TabContentProfile?.UpdateCardOpacity();
        TabContentThemeStore?.UpdateCardOpacity();
        TabContentMesh?.UpdateCardOpacity();
        TabContentDevices?.UpdateCardOpacity();
        TabContentFriends?.UpdateCardOpacity();
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
        TabContentMesh?.OnThemeChanged();
        TabContentDevices?.OnThemeChanged();
        TabContentFriends?.OnThemeChanged();
        TabContentPayment?.OnThemeChanged();
        TabContentDelete?.OnThemeChanged();
    }

    private void HandleThemeApplied(ThemeAppliedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (Color.TryParse(e.Manifest.Core.Colors.BgBase, out var bgBaseColor))
                {
                    ThisPage.BackgroundColor = bgBaseColor;
                    BackgroundColor = bgBaseColor;
#if ANDROID
                    if (OperatingSystem.IsAndroidVersionAtLeast(29))
                    {
                        MainActivity.UpdateSystemBarsTheme(bgBaseColor);
                    }
#endif
                }

                if (!string.IsNullOrEmpty(e.BackgroundImagePath) && File.Exists(e.BackgroundImagePath))
                {
                    ThemeBackgroundImage.Source = LoadLocalImage(e.BackgroundImagePath);
                    ThemeBackgroundImage.Opacity = e.Manifest.Background?.Opacity ?? 0.45;
                    ThemeBackgroundImage.IsVisible = true;
                }
                else
                {
                    ClearImageElement(ThemeBackgroundImage);
                }

                var hasTopDecorations = false;
                if (!string.IsNullOrEmpty(e.ScreenFramePath) && File.Exists(e.ScreenFramePath))
                {
                    ThemeScreenFrameOverlay.SetFrame(e.ScreenFramePath, e.FrameSlice);
                    hasTopDecorations = true;
                }
                else
                {
                    ThemeScreenFrameOverlay.Clear();
                }
                SetDecorationElement(ThemeCornerTopLeft, e.CornerTopLeftPath, ref hasTopDecorations);
                SetDecorationElement(ThemeCornerTopRight, e.CornerTopRightPath, ref hasTopDecorations);

                var hasBottomDecorations = false;
                SetDecorationElement(ThemeCornerBottomLeft, e.CornerBottomLeftPath, ref hasBottomDecorations);
                SetDecorationElement(ThemeCornerBottomRight, e.CornerBottomRightPath, ref hasBottomDecorations);

                var hasDecorations = hasTopDecorations || hasBottomDecorations;
                ThemeDecorationsContainer.IsVisible = hasDecorations;
                ThemeDecorationsContainer.Opacity = 1.0;

                MainContentContainer.Margin = new Thickness(0);

                if (e.HasParticles)
                {
                    _ = Color.TryParse(e.Manifest.Core.Colors.Primary, out var primary);
                    _ = Color.TryParse(e.Manifest.Core.Colors.Accent, out var accent);
                    ThemeParticles.Configure(e.ParticleSpritePath, e.Manifest.Vfx, primary, accent);
                }
                else
                {
                    ThemeParticles.StopAnimation();
                    ThemeParticles.IsVisible = false;
                }

                TabContentVpn?.ApplyThemeButton(
                    e.ButtonImageIdlePath,
                    e.ButtonImageConnectingPath,
                    e.ButtonImageActivePath,
                    e.ButtonImageErrorPath,
                    e.ButtonVideoIdlePath,
                    e.ButtonVideoConnectingPath,
                    e.ButtonVideoActivePath,
                    e.ButtonVideoErrorPath);

                try
                {
                    ThemeBackgroundVideo.Stop();
                }
                catch { }

                if (!string.IsNullOrEmpty(e.BackgroundVideoPath) && File.Exists(e.BackgroundVideoPath) && _themeManager.VideoEnabled)
                {
                    try
                    {
                        _isThemeVideoActive = true;
                        ThemeBackgroundVideo.ShouldShowPlaybackControls = false;
                        ThemeBackgroundVideo.Opacity = 1.0;
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
                        ThemeBackgroundVideoDimmer.Color = Color.FromArgb("#000000");
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
                        ThemeBackgroundVideo.IsVisible = false;
                        ThemeBackgroundVideo.Source = null;
                        ThemeBackgroundVideoDimmer.IsVisible = false;
                    }
                    catch { }
                }

                NotifyViewsThemeChanged();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainPage] HandleThemeApplied error: {ex.Message}");
            }
        });
    }

    private void HandleThemeReset()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var defaultBg = (Color)(Application.Current?.Resources["BgBase"] ?? Color.FromArgb("#0E0E14"));
            ThisPage.BackgroundColor = defaultBg;
            BackgroundColor = defaultBg;
#if ANDROID
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                MainActivity.UpdateSystemBarsTheme(defaultBg);
            }
#endif

            ClearImageElement(ThemeBackgroundImage);
            ThemeScreenFrameOverlay.Clear();
            ClearImageElement(ThemeCornerTopLeft);
            ClearImageElement(ThemeCornerTopRight);
            ClearImageElement(ThemeCornerBottomLeft);
            ClearImageElement(ThemeCornerBottomRight);
            ThemeDecorationsContainer.IsVisible = false;
            ThemeDecorationsContainer.Opacity = 1.0;
            _isDecorationsFaded = false;
            MainContentContainer.Margin = new Thickness(0);

            ThemeParticles.StopAnimation();
            ThemeParticles.IsVisible = false;

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
        if (ThemeDecorationsContainer.IsVisible && _isDecorationsFaded)
        {
            _isDecorationsFaded = false;
            ThemeDecorationsContainer.CancelAnimations();
            _ = ThemeDecorationsContainer.FadeToAsync(1.0, 180, Easing.CubicIn);
        }
    }

    private void OnPagePointerMoved(object? sender, PointerEventArgs e)
    {
        var pos = e.GetPosition(ThisPage);
        if (pos.HasValue)
        {
            UpdateDecorationFade(pos.Value.X, pos.Value.Y, ThisPage.Width, ThisPage.Height);
        }
    }

    private void OnPagePointerExited(object? sender, PointerEventArgs e) =>
        RestoreDecorationsOpacity();

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
        TabContentMesh?.SetHeaderTopInset(safeTop);
        TabContentFriends?.SetHeaderTopInset(safeTop);
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

#if WINDOWS
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
