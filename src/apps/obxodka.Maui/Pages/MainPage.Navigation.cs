namespace obxodka.Pages;

public sealed partial class MainPage
{
    private void OnSafeAreaInsetsChanged(double top, double bottom) =>
        ApplySafeArea(top, bottom);

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
        await UiAnimations.SwitchViewAsync(outgoing, incoming, animate);
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
            }
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
}
