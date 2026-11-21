namespace obxodka.Pages;

public sealed partial class MainPage
{
    private double _pendingLayoutWidth;
    private double _pendingLayoutHeight;
    private bool _isLayoutDispatchPending;

    private void OnSafeAreaInsetsChanged(double top, double bottom) =>
        ApplySafeArea(top, bottom);

    private void OnSidebarWidthChanged(object? sender, EventArgs e)
    {
        if (!_isOverlaySidebar)
        {
            RefreshAdaptiveLayout(forceApply: true);
        }
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        //откладываем перестроение колонок за пределы текущего onlayout прохода android (исключает requestlayout during layout)
        _pendingLayoutWidth = width;
        _pendingLayoutHeight = height;
        if (_isLayoutDispatchPending)
        {
            return;
        }

        _isLayoutDispatchPending = true;
        _ = Dispatcher.Dispatch(() =>
        {
            _isLayoutDispatchPending = false;
            if (_pendingLayoutWidth > 0 && _pendingLayoutHeight > 0)
            {
                RefreshAdaptiveLayout(overrideWidth: _pendingLayoutWidth, overrideHeight: _pendingLayoutHeight);
            }
        });
    }

    private bool ShouldShowNavigationChrome() =>
        !string.IsNullOrEmpty(_activeTab) && _activeTab != "auth";

    private void RefreshAdaptiveLayout(bool forceApply = false, double? overrideWidth = null, double? overrideHeight = null)
    {
        var windowWidth = overrideWidth is > 0 ? overrideWidth.Value : Width;
        var windowHeight = overrideHeight is > 0 ? overrideHeight.Value : Height;
        var isTablet = DeviceInfo.Idiom == DeviceIdiom.Tablet;
        var isDesktopOrTablet = DeviceInfo.Idiom == DeviceIdiom.Desktop || isTablet;
        var showNav = ShouldShowNavigationChrome();
        var safeTop = _lastAppliedTopInset > 0 ? _lastAppliedTopInset : Math.Max(0, SafeAreaHelper.TopInset);
        var safeBottom = _lastAppliedBottomInset > 0 ? _lastAppliedBottomInset : Math.Max(0, SafeAreaHelper.BottomInset);
        var dockedWidth = DesktopSidebar.OccupiedWidth > 0
            ? DesktopSidebar.OccupiedWidth
            : AdaptiveLayoutHelper.CollapsedSidebarRailWidth;

        var rawViewport = AdaptiveLayoutHelper.CreateViewport(
            windowWidth,
            windowHeight,
            safeTop,
            safeBottom,
            isDesktopOrTablet,
            isTablet,
            dockedWidth);

        //на экране входа сайдбар скрыт и контент занимает всю ширину окна
        var viewport = showNav
            ? rawViewport
            : rawViewport with
            {
                ContentWidth = windowWidth > 0 ? windowWidth : rawViewport.ContentWidth,
                UseSidebar = false,
                UseOverlaySidebar = false
            };

        var chromeChanged = _isWideLayout != viewport.UseSidebar ||
                            _isOverlaySidebar != viewport.UseOverlaySidebar ||
                            _isNavChromeVisible != showNav;
        var viewportChanged = _currentViewport != viewport;

        if (!forceApply && !chromeChanged && !viewportChanged)
        {
            return;
        }

        var wasNavVisible = _isNavChromeVisible == true;
        var wasWideLayout = _isWideLayout == true;
        _isWideLayout = viewport.UseSidebar;
        _isOverlaySidebar = viewport.UseOverlaySidebar;
        _isNavChromeVisible = showNav;
        _currentViewport = viewport;

        if (chromeChanged || forceApply)
        {
            ApplyAdaptiveLayout(viewport.UseSidebar, viewport.UseOverlaySidebar, showNav, wasNavVisible, wasWideLayout);
        }

        if (viewport.UseSidebar)
        {
            DesktopSidebar.SetCompactHeightMode(viewport.IsShortScreen);
        }
        else
        {
            MobileBottomBar.SetCompactMode(viewport.IsShortScreen);
        }

        BroadcastViewportToTabs();
    }

    private void ApplyAdaptiveLayout(bool useSidebar, bool useOverlay, bool showNav, bool wasNavVisible, bool wasWideLayout)
    {
        if (!showNav)
        {
            DesktopSidebar.Collapse();
            DesktopSidebar.IsVisible = false;
            DesktopSidebar.HideSidebar();
            MobileBottomBar.IsVisible = false;
            MobileBottomBar.HideSidebar();
            _ = SetSidebarOverlayBackdropAsync(false);
            RootShellGrid.ColumnDefinitions[0].Width = new GridLength(0);
            return;
        }

        if (useSidebar)
        {
            MobileBottomBar.IsVisible = false;
            MobileBottomBar.HideSidebar();
            DesktopSidebar.IsVisible = true;
            DesktopSidebar.SetOverlayMode(useOverlay);

            if (useOverlay)
            {
                RootShellGrid.ColumnDefinitions[0].Width = new GridLength(AdaptiveLayoutHelper.CollapsedSidebarRailWidth);
                Grid.SetColumnSpan(DesktopSidebar, 2);
                DesktopSidebar.HorizontalOptions = LayoutOptions.Start;
                DesktopSidebar.ZIndex = 60;
                _ = SetSidebarOverlayBackdropAsync(DesktopSidebar.IsExpanded);
            }
            else
            {
                RootShellGrid.ColumnDefinitions[0].Width = GridLength.Auto;
                Grid.SetColumnSpan(DesktopSidebar, 1);
                DesktopSidebar.HorizontalOptions = LayoutOptions.Fill;
                DesktopSidebar.ZIndex = 10;
                _ = SetSidebarOverlayBackdropAsync(false);
            }

            if (!wasNavVisible || !wasWideLayout)
            {
                _ = DesktopSidebar.PlayEntranceAnimationAsync();
            }
        }
        else
        {
            DesktopSidebar.Collapse();
            DesktopSidebar.IsVisible = false;
            DesktopSidebar.HideSidebar();
            _ = SetSidebarOverlayBackdropAsync(false);
            RootShellGrid.ColumnDefinitions[0].Width = new GridLength(0);
            MobileBottomBar.IsVisible = true;
            if (!wasNavVisible || wasWideLayout)
            {
                _ = MobileBottomBar.PlayEntranceAnimationAsync();
            }
        }
    }

    //единая рассылка актуального снимка геометрии всем открытым вкладкам
    private void BroadcastViewportToTabs()
    {
        foreach (var child in MainContentContainer.Children)
        {
            if (child is IAdaptivePage adaptivePage)
            {
                adaptivePage.ApplyViewport(_currentViewport);
            }
        }
    }

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
        RefreshAdaptiveLayout();

        if (_isOverlaySidebar && DesktopSidebar.IsExpanded)
        {
            DesktopSidebar.Collapse();
        }

        DesktopSidebar.UpdateActiveTab(tab);
        MobileBottomBar.UpdateActiveTab(tab);

        var outgoing = GetTabContent(prevTab);
        var incoming = EnsureTabCreated(tab);
        if (incoming is IAdaptivePage adaptiveIncoming)
        {
            adaptiveIncoming.ApplyViewport(_currentViewport);
        }

        await UiAnimations.SwitchViewAsync(outgoing, incoming, animate);
        UpdateAllViewsOpacity();

        switch (tab)
        {
            case "vpn":
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
                _tabContentConfiguration?.OnAppearing();
                if (animate)
                {
                    _ = _tabContentConfiguration?.PlayEntranceAnimationAsync();
                }

                break;
            case "profile":
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
                if (_tabContentThemeStore is not null)
                {
                    if (animate)
                    {
                        _ = _tabContentThemeStore.PlayEntranceAnimationAsync();
                    }

                    _ = _tabContentThemeStore.LoadCatalogAsync();
                }

                break;
        }
    }

    //ленивое создание и инициализация вкладки только при первом обращении
    private View EnsureTabCreated(string tab)
    {
        while (true)
        {
            switch (tab)
            {
                case "vpn":
                    if (_tabContentVpn is null)
                    {
                        _tabContentVpn = new VpnView
                        {
                            IsVisible = false
                        };
                        MainContentContainer.Children.Add(_tabContentVpn);
                    }

                    if (_isVpnTabInitialized)
                    {
                        return _tabContentVpn;
                    }

                    _isVpnTabInitialized = true;
                    _tabContentVpn.Initialize(this, VpnService, _apiService);
                    ApplyInitialDecorations(_tabContentVpn);
                    if (_lastAppliedThemeArgs is { } themeArgs)
                    {
                        ApplyThemeToVpnTab(_tabContentVpn, themeArgs);
                        _tabContentVpn.SetCardFrameVisibility(_themeManager.FramesEnabled);
                    }

                    _tabContentVpn.UpdateBalanceUi(TimeFormatHelper.FormatSeconds(RemainingSeconds, false));

                    return _tabContentVpn;

                case "auth":
                    if (_tabContentAuth is not null)
                    {
                        return _tabContentAuth;
                    }

                    _tabContentAuth = new AuthView
                    {
                        IsVisible = false
                    };
                    _tabContentAuth.Initialize(this, _apiService, _qrScanner);
                    ApplyInitialDecorations(_tabContentAuth);
                    MainContentContainer.Children.Add(_tabContentAuth);

                    return _tabContentAuth;

                case "configuration":
                    if (_tabContentConfiguration is not null)
                    {
                        return _tabContentConfiguration;
                    }

                    _tabContentConfiguration = new ConfigurationView
                    {
                        IsVisible = false
                    };
                    _tabContentConfiguration.ThemesRequested += OnConfigurationThemesRequested;
                    ApplyInitialDecorations(_tabContentConfiguration);
                    MainContentContainer.Children.Add(_tabContentConfiguration);

                    return _tabContentConfiguration;

                case "profile":
                    if (_tabContentProfile is not null)
                    {
                        return _tabContentProfile;
                    }

                    _tabContentProfile = new ProfileView
                    {
                        IsVisible = false
                    };
                    _tabContentProfile.Initialize(this, _themeManager, _apiService);
                    _tabContentProfile.LogoutRequested += OnProfileLogoutRequestedAsync;
                    _tabContentProfile.BuyTokensRequested += OnBuyTokensRequestedAsync;
                    _tabContentProfile.BalanceRefreshRequested += OnProfileBalanceRefreshRequestedAsync;
                    _tabContentProfile.UpdateBalance(RemainingSeconds);
                    ApplyInitialDecorations(_tabContentProfile);
                    MainContentContainer.Children.Add(_tabContentProfile);

                    return _tabContentProfile;

                case "devices":
                    if (_tabContentDevices is not null)
                    {
                        return _tabContentDevices;
                    }

                    _tabContentDevices = new DevicesView
                    {
                        IsVisible = false
                    };
                    _tabContentDevices.Initialize(this, _apiService);
                    ApplyInitialDecorations(_tabContentDevices);
                    MainContentContainer.Children.Add(_tabContentDevices);

                    return _tabContentDevices;

                case "split":
                    if (_tabContentSplit is not null)
                    {
                        return _tabContentSplit;
                    }

                    _tabContentSplit = new SplitView
                    {
                        IsVisible = false
                    };
                    _tabContentSplit.Initialize(this, _appManager);
                    ApplyInitialDecorations(_tabContentSplit);
                    MainContentContainer.Children.Add(_tabContentSplit);

                    return _tabContentSplit;

                case "delete":
                    if (_tabContentDelete is not null)
                    {
                        return _tabContentDelete;
                    }

                    _tabContentDelete = new DeleteAccountView
                    {
                        IsVisible = false
                    };
                    _tabContentDelete.Initialize(this, _apiService);
                    _tabContentDelete.CancelRequested += OnDeleteCancelRequested;
                    _tabContentDelete.AccountDeleted += OnAccountDeletedAsync;
                    ApplyInitialDecorations(_tabContentDelete);
                    MainContentContainer.Children.Add(_tabContentDelete);

                    return _tabContentDelete;

                case "payment":
                    if (_tabContentPayment is not null)
                    {
                        return _tabContentPayment;
                    }

                    _tabContentPayment = new PaymentView
                    {
                        IsVisible = false
                    };
                    _tabContentPayment.Initialize(this, _apiService);
                    _tabContentPayment.PaymentCompleted += OnPaymentCompletedAsync;
                    _tabContentPayment.PaymentCancelled += OnPaymentCancelled;
                    ApplyInitialDecorations(_tabContentPayment);
                    MainContentContainer.Children.Add(_tabContentPayment);

                    return _tabContentPayment;

                case "themes":
                    if (_tabContentThemeStore is not null)
                    {
                        return _tabContentThemeStore;
                    }

                    _tabContentThemeStore = new ThemeStoreView
                    {
                        IsVisible = false
                    };
                    _tabContentThemeStore.Initialize(_themeManager);
                    _tabContentThemeStore.BackRequested += OnThemeStoreBackRequested;
                    ApplyInitialDecorations(_tabContentThemeStore);
                    MainContentContainer.Children.Add(_tabContentThemeStore);

                    return _tabContentThemeStore;

                default:
                    tab = "vpn";
                    continue;
            }
        }
    }

    //применение актуальной геометрии и темы на лениво созданную вкладку
    private void ApplyInitialDecorations(VisualElement view)
    {
        if (view is IAdaptivePage adaptive)
        {
            adaptive.ApplyViewport(_currentViewport);
        }

        UpdateAllViewsOpacity();
    }

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
        DesktopSidebar.ApplySafeAreaInsets(top, bottom);
        MobileBottomBar.ApplyBottomInset(bottom);
        RefreshAdaptiveLayout(forceApply: true);
    }
}
