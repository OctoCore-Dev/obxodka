using Microsoft.Maui.Controls.Shapes;

namespace obxodka.Views;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "ContentView lifecycle is managed by MAUI visual tree")]
public sealed partial class SplitView : ContentView
{
    private static Color ActiveChipColor => (Application.Current?.Resources.TryGetValue("Primary", out var val) == true && val is Color c)
        ? c
        : Color.FromArgb("#7C3AED");
    private static Color ActiveChipBright => (Application.Current?.Resources.TryGetValue("PrimaryBright", out var val) == true && val is Color c)
        ? c
        : ActiveChipColor;
    private static Color ActiveChipBg => (Application.Current?.Resources.TryGetValue("PrimaryDim", out var pd) == true && pd is Color pdc)
        ? pdc
        : ActiveChipColor.WithAlpha(0.18f);
    private static Color InactiveChipBg => (Application.Current?.Resources.TryGetValue("BgSurface", out var bs) == true && bs is Color bsc)
        ? bsc
        : Color.FromArgb("#15FFFFFF");
    private static Color InactiveChipStroke => (Application.Current?.Resources.TryGetValue("BorderSubtle", out var bst) == true && bst is Color bstc)
        ? bstc
        : Color.FromArgb("#30FFFFFF");
    private static Color InactiveChipText => (Application.Current?.Resources.TryGetValue("TextSecondary", out var ts) == true && ts is Color tsc)
        ? tsc
        : Color.FromArgb("#9CA3AF");

    private static Color ErrorColor => (Application.Current?.Resources.TryGetValue("Error", out var val) == true && val is Color c)
        ? c
        : Color.FromArgb("#EF4444");

    private MainPage _parent = null!;
    private DesktopSidebarView? _sidebar;
    private IAppManager _appManager = null!;

    private List<AppInfoItem> _allApps = [];
    private readonly Dictionary<string, Border> _activeChipBorders = [with(StringComparer.OrdinalIgnoreCase)];
    private bool _showOnlyBypassed;
    private bool _isSitesTabActive;
    private bool _isUpdatingInternally;
    private CancellationTokenSource? _saveDebounceCts;
    private CancellationTokenSource? _searchDebounceCts;
#if WINDOWS
    private CancellationTokenSource? _routesDebounceCts;
#endif

    public static readonly BindableProperty IsSplitEditingAllowedProperty =
        BindableProperty.Create(nameof(IsSplitEditingAllowed), typeof(bool), typeof(SplitView), false);

    public bool IsSplitEditingAllowed
    {
        get => (bool)GetValue(IsSplitEditingAllowedProperty);
        set => SetValue(IsSplitEditingAllowedProperty, value);
    }

    public SplitView()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
        RootLayout.SizeChanged += OnRootLayoutSizeChanged;
        SitesSplitScrollView.Scrolled += OnSitesScrollViewScrolled;
#if WINDOWS
        ModeTabsGrid.IsVisible = true;
        ChipAddExe.IsVisible = true;
        AndroidWarningBanner.IsVisible = false;
        IsSplitEditingAllowed = true;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            Platforms.Windows.WindowsSplitTunnelEngine.OnRoutesUpdated += OnWindowsRoutesUpdated;
        }
        LoadWindowsSitesAndPresetsState();
#endif
    }

    public void Initialize(MainPage parent, IAppManager appManager)
    {
        _parent = parent;
        _appManager = appManager;
        _parent.VpnService.OnStateChanged += OnVpnStateChanged;
        _parent.SizeChanged += OnParentOrSidebarSizeChanged;
        _sidebar = _parent.FindByName<DesktopSidebarView>("DesktopSidebar");
        _sidebar?.SidebarWidthChanged += OnParentOrSidebarSizeChanged;
        UpdateRestartWarningVisibility(_parent.VpnService.CurrentState);
    }

    private void OnParentOrSidebarSizeChanged(object? sender, EventArgs e)
    {
        if (IsVisible)
        {
            ForceLayoutWidth();
        }
    }

    private double ComputeAvailableContentWidth()
    {
        if (RootLayout is null)
        {
            return 0;
        }

        var pad = RootLayout.Padding.HorizontalThickness;
        if (_parent is not null && _parent.Width > 0 && DeviceInfo.Idiom != DeviceIdiom.Phone)
        {
            var sidebarW = _sidebar?.OccupiedWidth ?? 0;
            var fromParent = _parent.Width - sidebarW - pad;
            if (fromParent > 0)
            {
                return fromParent;
            }
        }

        if (RootLayout.Width > 0)
        {
            var fromRoot = RootLayout.Width - pad;
            if (fromRoot > 0)
            {
                return fromRoot;
            }
        }

        return Width > 0 ? Math.Max(0, Width - pad) : 0;
    }

    public void ForceLayoutWidth()
    {
        var avail = ComputeAvailableContentWidth();
        if (avail > 0)
        {
            ApplyCardWidth(avail);
        }
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        ForceLayoutWidth();
    }

    private void OnRootLayoutSizeChanged(object? sender, EventArgs e) => ForceLayoutWidth();

    private void ApplyCardWidth(double targetWidth)
    {
        if (targetWidth <= 0 || MainContentGrid is null)
        {
            return;
        }

        if (DeviceInfo.Idiom == DeviceIdiom.Phone)
        {
            MainContentGrid.ClearValue(WidthRequestProperty);
            MainContentGrid.ClearValue(MaximumWidthRequestProperty);
            SplitAppsList?.ClearValue(WidthRequestProperty);
            SplitAppsList?.ClearValue(MaximumWidthRequestProperty);
            SitesSplitScrollView?.ClearValue(WidthRequestProperty);
            SitesSplitScrollView?.ClearValue(MaximumWidthRequestProperty);
            HeaderContainer?.ClearValue(WidthRequestProperty);
            HeaderContainer?.ClearValue(MaximumWidthRequestProperty);
            ActiveBypassedScrollView?.ClearValue(MaximumWidthRequestProperty);
            return;
        }

        var safeWidth = Math.Min(targetWidth - 6, 950);
        if (safeWidth <= 0)
        {
            return;
        }

        MainContentGrid.WidthRequest = safeWidth;
        MainContentGrid.MaximumWidthRequest = safeWidth;
        if (SplitAppsList != null)
        {
            SplitAppsList.WidthRequest = safeWidth;
            SplitAppsList.MaximumWidthRequest = safeWidth;
        }
        if (SitesSplitScrollView != null)
        {
            SitesSplitScrollView.WidthRequest = safeWidth;
            SitesSplitScrollView.MaximumWidthRequest = safeWidth;
        }
        if (HeaderContainer != null)
        {
            HeaderContainer.WidthRequest = safeWidth;
            HeaderContainer.MaximumWidthRequest = safeWidth;
        }
        ActiveBypassedScrollView?.MaximumWidthRequest = safeWidth;
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        RootLayout.SizeChanged -= OnRootLayoutSizeChanged;
        SitesSplitScrollView.Scrolled -= OnSitesScrollViewScrolled;
        if (_parent is not null)
        {
            _parent.VpnService.OnStateChanged -= OnVpnStateChanged;
            _parent.SizeChanged -= OnParentOrSidebarSizeChanged;
        }
        _sidebar?.SidebarWidthChanged -= OnParentOrSidebarSizeChanged;
#if WINDOWS
        _routesDebounceCts?.Cancel();
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            Platforms.Windows.WindowsSplitTunnelEngine.OnRoutesUpdated -= OnWindowsRoutesUpdated;
        }
#endif
    }

    private void OnSitesScrollViewScrolled(object? sender, ScrolledEventArgs e)
    {
        if (SitesSplitScrollView.Content is VerticalStackLayout stack && SitesSplitScrollView.Height > 0)
        {
            CardStackScrollHelper.ApplyToChildren(
                stack.Children.OfType<VisualElement>(),
                e.ScrollY,
                SitesSplitScrollView.Height,
                zone: 65.0,
                minScale: 0.92,
                power: 2.5);
        }
    }

    public async Task PlayEntranceAnimationAsync()
    {
        Opacity = 1;
        TranslationY = 0;
        if (_isSitesTabActive)
        {
            await UIAnimations.PlayEntranceCascadeAsync(
                30,
                230,
                HeaderContainer,
                SitesInfoBanner,
                PresetGovCard,
                PresetBanksCard,
                PresetMediaCard,
                PresetMarketCard,
                PresetRuTldsCard,
                CustomRulesCard,
                ActiveRoutesCard);
        }
        else
        {
            await UIAnimations.PlayEntranceCascadeAsync(35, 220, HeaderContainer, SplitAppsList);
        }
    }

#if WINDOWS
    private void OnWindowsRoutesUpdated()
    {
        _routesDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _routesDebounceCts = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(220, token).ConfigureAwait(false);
                if (!token.IsCancellationRequested)
                {
                    MainThread.BeginInvokeOnMainThread(UpdateWindowsDynamicRouteCounter);
                }
            }
            catch { }
        }, token);
    }
#endif

    private void OnVpnStateChanged(AppVpnState s) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateRestartWarningVisibility(s);
#if WINDOWS
            IsSplitEditingAllowed = true;
            UpdateWindowsDynamicRouteCounter();
#else
            if (_allApps.Count > 0)
            {
                IsSplitEditingAllowed = s == AppVpnState.Disconnected;
                AndroidWarningBanner.IsVisible = !IsSplitEditingAllowed;
            }
#endif
        });

    public async Task LoadSplitAppsAsync()
    {
        ForceLayoutWidth();
        UpdateRestartWarningVisibility(_parent.VpnService.CurrentState);
#if WINDOWS
        LoadWindowsSitesAndPresetsState();
#endif
        if (_allApps.Count > 0)
        {
            var bypassed = _appManager.GetBypassedPackages().ToHashSet(StringComparer.OrdinalIgnoreCase);
            _isUpdatingInternally = true;
            foreach (var app in _allApps)
            {
                app.IsBypassed = bypassed.Contains(app.PackageName);
            }
            _isUpdatingInternally = false;

            _allApps =
            [
                .. _allApps
                    .Where(a => !Core.Models.SplitTunnelPolicy.IsProtectedObxodkaEntry(a.Name) &&
                                !Core.Models.SplitTunnelPolicy.IsProtectedObxodkaEntry(a.PackageName))
                    .OrderByDescending(a => a.IsBypassed)
                    .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            ];

            UpdateFilterChipStyles();
            RebuildAllBypassedChips();
            UpdateCountersOnly();
            ApplyFilterAndRenderList();
#if WINDOWS
            IsSplitEditingAllowed = true;
#else
            IsSplitEditingAllowed = _parent.VpnService.CurrentState == AppVpnState.Disconnected;
            AndroidWarningBanner.IsVisible = !IsSplitEditingAllowed;
#endif
            return;
        }

        SplitLoadingOverlay.IsVisible = true;
        if (!_isSitesTabActive)
        {
            SplitAppsList.IsVisible = false;
        }
        _ = HideSplitToastAsync();

        try
        {
            var apps = await _appManager.GetInstalledAppsAsync();
            if (apps is not null)
            {
                _allApps =
                [
                    .. apps
                        .Where(a => !Core.Models.SplitTunnelPolicy.IsProtectedObxodkaEntry(a.Name) &&
                                    !Core.Models.SplitTunnelPolicy.IsProtectedObxodkaEntry(a.PackageName))
                        .OrderByDescending(a => a.IsBypassed)
                        .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                ];
                UpdateFilterChipStyles();
                RebuildAllBypassedChips();
                UpdateCountersOnly();
                ApplyFilterAndRenderList();
#if WINDOWS
                IsSplitEditingAllowed = true;
#else
                IsSplitEditingAllowed = _parent.VpnService.CurrentState == AppVpnState.Disconnected;
                AndroidWarningBanner.IsVisible = !IsSplitEditingAllowed;
#endif
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SPLIT ERROR] {ex.Message}");
            await ShowSplitErrorAsync("Ошибка загрузки приложений.");
#if !WINDOWS
            IsSplitEditingAllowed = false;
#endif
        }
        finally
        {
            SplitLoadingOverlay.IsVisible = false;
            if (!_isSitesTabActive)
            {
                SplitAppsList.IsVisible = true;
            }
        }
    }

    private async void OnRefreshAppsClickedAsync(object? sender, EventArgs e)
    {
        ThemeAudioService.PlayClick();
        _ = ChipRefreshApps.BounceClickAsync();
        _allApps.Clear();
        _activeChipBorders.Clear();
        ActiveBypassedChipsLayout.Children.Clear();
        SplitAppsList.ItemsSource = null;
        await LoadSplitAppsAsync();
    }

    private void OnTabModeAppsClicked(object? sender, EventArgs e)
    {
        if (!_isSitesTabActive)
        {
            return;
        }

        ThemeAudioService.PlayClick();
        _ = TabModeApps.BounceClickAsync();
        _toastCts?.Cancel();
        _ = HideSplitToastAsync();
        _isSitesTabActive = false;
        AppsHeaderControls.IsVisible = true;
        SplitAppsList.IsVisible = true;
        SitesSplitScrollView.IsVisible = false;
        UpdateModeTabStyles();
        UpdateFilterChipStyles();
        ApplyFilterAndRenderList();
        _ = UIAnimations.PlayEntranceCascadeAsync(30, 200, AppsHeaderControls, SplitAppsList);
    }

    private void OnTabModeSitesClicked(object? sender, EventArgs e)
    {
        if (_isSitesTabActive)
        {
            return;
        }

        ThemeAudioService.PlayClick();
        _ = TabModeSites.BounceClickAsync();
        _toastCts?.Cancel();
        _ = HideSplitToastAsync();
        _isSitesTabActive = true;
        AppsHeaderControls.IsVisible = false;
        SplitAppsList.IsVisible = false;
        SitesSplitScrollView.IsVisible = true;
#if WINDOWS
        LoadWindowsSitesAndPresetsState();
#endif
        UpdateModeTabStyles();
        _ = UIAnimations.PlayEntranceCascadeAsync(
            25,
            210,
            SitesInfoBanner,
            PresetGovCard,
            PresetBanksCard,
            PresetMediaCard,
            PresetMarketCard,
            PresetRuTldsCard,
            CustomRulesCard,
            ActiveRoutesCard);
    }

    private void UpdateModeTabStyles()
    {
        if (_isSitesTabActive)
        {
            TabModeApps.BackgroundColor = InactiveChipBg;
            TabModeApps.Stroke = InactiveChipStroke;
            TabModeAppsLabel.TextColor = InactiveChipText;

            TabModeSites.BackgroundColor = ActiveChipBg;
            TabModeSites.Stroke = ActiveChipColor;
            TabModeSitesLabel.TextColor = ActiveChipBright;
        }
        else
        {
            TabModeApps.BackgroundColor = ActiveChipBg;
            TabModeApps.Stroke = ActiveChipColor;
            TabModeAppsLabel.TextColor = ActiveChipBright;

            TabModeSites.BackgroundColor = InactiveChipBg;
            TabModeSites.Stroke = InactiveChipStroke;
            TabModeSitesLabel.TextColor = InactiveChipText;
        }
    }

#if WINDOWS
    private void LoadWindowsSitesAndPresetsState()
    {
        _isUpdatingInternally = true;
        try
        {
            PresetGovSwitch.IsToggled = Preferences.Get(Platforms.Windows.AppManager.PresetGovKey, true);
            PresetBanksSwitch.IsToggled = Preferences.Get(Platforms.Windows.AppManager.PresetBanksKey, true);
            PresetMediaSwitch.IsToggled = Preferences.Get(Platforms.Windows.AppManager.PresetMediaKey, false);
            PresetMarketSwitch.IsToggled = Preferences.Get(Platforms.Windows.AppManager.PresetMarketKey, false);
            PresetRuTldsSwitch.IsToggled = Preferences.Get(Platforms.Windows.AppManager.PresetRuTldsKey, false);
        }
        finally
        {
            _isUpdatingInternally = false;
        }

        RenderCustomRulesChips();
        UpdateWindowsDynamicRouteCounter();
    }

    private void UpdateWindowsDynamicRouteCounter()
    {
        var count = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
            ? Platforms.Windows.WindowsSplitTunnelEngine.ActiveRouteCount
            : 0;
        var rulesCount = Platforms.Windows.AppManager.GetCustomRules().Count;
        var activePresetsCount =
            (PresetGovSwitch.IsToggled ? 1 : 0) +
            (PresetBanksSwitch.IsToggled ? 1 : 0) +
            (PresetMediaSwitch.IsToggled ? 1 : 0) +
            (PresetMarketSwitch.IsToggled ? 1 : 0) +
            (PresetRuTldsSwitch.IsToggled ? 1 : 0);
        var totalConfigured = rulesCount + activePresetsCount;
        TabModeSitesLabel.Text = totalConfigured > 0 ? $"Сайты и Сервисы РФ ({totalConfigured})" : "Сайты и Сервисы РФ";
        ActiveDynamicRoutesLabel.Text = count > 0 ? $"{count} маршр." : "Готово";
        RenderActiveBypassRoutesChips();
    }

    private void RenderActiveBypassRoutesChips()
    {
        if (ActiveRoutesFlexLayout is null || ActiveRoutesEmptyLabel is null || !IsVisible || !_isSitesTabActive)
        {
            return;
        }

        ActiveRoutesFlexLayout.Children.Clear();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            ActiveRoutesEmptyLabel.IsVisible = true;
            ActiveRoutesFlexLayout.IsVisible = false;
            return;
        }

        var routes = Platforms.Windows.WindowsSplitTunnelEngine.ActiveBypassRoutes;
        if (routes.Count == 0)
        {
            var isConnected = _parent?.VpnService.CurrentState == AppVpnState.Connected;
            ActiveRoutesEmptyLabel.Text = isConnected
                ? "Ожидание трафика: маршруты добавляются мгновенно при обращении к выбранным сайтам или приложениям."
                : "Маршруты появятся автоматически при подключении VPN и открытии выбранных сайтов или приложений.";
            ActiveRoutesEmptyLabel.IsVisible = true;
            ActiveRoutesFlexLayout.IsVisible = false;
            return;
        }

        ActiveRoutesEmptyLabel.IsVisible = false;
        ActiveRoutesFlexLayout.IsVisible = true;

        const int maxVisibleChips = 28;
        var displayed = routes.Take(maxVisibleChips).ToList();

        foreach (var (ip, reason) in displayed)
        {
            var chip = new Border
            {
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(9, 5),
                BackgroundColor = InactiveChipBg,
                Stroke = InactiveChipStroke,
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 10 }
            };

            var row = new HorizontalStackLayout
            {
                Spacing = 5,
                VerticalOptions = LayoutOptions.Center
            };

            var isExe = reason.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            row.Children.Add(new MauiIcon
            {
                Icon = isExe ? FluentIcons.Apps16 : FluentIcons.ArrowRouting20,
                IconColor = ActiveChipBright,
                IconSize = 13,
                VerticalOptions = LayoutOptions.Center
            });

            var text = string.Equals(reason, ip, StringComparison.OrdinalIgnoreCase)
                ? ip
                : $"{reason} ({ip})";

            var lbl = new Label
            {
                Text = text,
                FontSize = 11,
                TextColor = InactiveChipText,
                VerticalOptions = LayoutOptions.Center
            };
            lbl.SetDynamicResource(Label.FontFamilyProperty, "AppFontMedium");
            row.Children.Add(lbl);

            chip.Content = row;
            ActiveRoutesFlexLayout.Children.Add(chip);
        }

        if (routes.Count > maxVisibleChips)
        {
            var moreChip = new Border
            {
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(9, 5),
                BackgroundColor = ActiveChipBg,
                Stroke = ActiveChipColor,
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 10 }
            };

            var moreLbl = new Label
            {
                Text = $"+{routes.Count - maxVisibleChips} ещё",
                FontSize = 11,
                TextColor = ActiveChipBright,
                VerticalOptions = LayoutOptions.Center
            };
            moreLbl.SetDynamicResource(Label.FontFamilyProperty, "AppFontBold");
            moreChip.Content = moreLbl;
            ActiveRoutesFlexLayout.Children.Add(moreChip);
        }
    }
#endif

    private void OnPresetGovCardTapped(object? sender, EventArgs e) =>
        PresetGovSwitch.IsToggled = !PresetGovSwitch.IsToggled;

    private void OnPresetBanksCardTapped(object? sender, EventArgs e) =>
        PresetBanksSwitch.IsToggled = !PresetBanksSwitch.IsToggled;

    private void OnPresetMediaCardTapped(object? sender, EventArgs e) =>
        PresetMediaSwitch.IsToggled = !PresetMediaSwitch.IsToggled;

    private void OnPresetMarketCardTapped(object? sender, EventArgs e) =>
        PresetMarketSwitch.IsToggled = !PresetMarketSwitch.IsToggled;

    private void OnPresetRuTldsCardTapped(object? sender, EventArgs e) =>
        PresetRuTldsSwitch.IsToggled = !PresetRuTldsSwitch.IsToggled;

    private void OnRefreshSplitRoutesClicked(object? sender, EventArgs e)
    {
#if WINDOWS
        ThemeAudioService.PlayClick();
        Platforms.Windows.AppManager.SyncPolicyFromPreferences();
        UpdateWindowsDynamicRouteCounter();
#endif
    }

    private void OnPresetSwitchToggled(object? sender, ToggledEventArgs e)
    {
        if (_isUpdatingInternally)
        {
            return;
        }

#if WINDOWS
        ThemeAudioService.PlayClick();
        Preferences.Set(Platforms.Windows.AppManager.PresetGovKey, PresetGovSwitch.IsToggled);
        Preferences.Set(Platforms.Windows.AppManager.PresetBanksKey, PresetBanksSwitch.IsToggled);
        Preferences.Set(Platforms.Windows.AppManager.PresetMediaKey, PresetMediaSwitch.IsToggled);
        Preferences.Set(Platforms.Windows.AppManager.PresetMarketKey, PresetMarketSwitch.IsToggled);
        Preferences.Set(Platforms.Windows.AppManager.PresetRuTldsKey, PresetRuTldsSwitch.IsToggled);
        Platforms.Windows.AppManager.SyncPolicyFromPreferences();
        UpdateWindowsDynamicRouteCounter();
#endif
    }

    private void OnAddCustomRuleClicked(object? sender, EventArgs e)
    {
#if WINDOWS
        var raw = CustomRuleEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        if (Core.Models.SplitTunnelPolicy.IsProtectedObxodkaEntry(raw))
        {
            _ = ShowSplitErrorAsync("Нельзя добавить домены или ресурсы obxodka в список исключений.");
            return;
        }

        var normalized = Core.Models.SplitTunnelPolicy.NormalizeRuleEntry(raw);
        if (Core.Models.SplitTunnelPolicy.IsProtectedObxodkaEntry(normalized))
        {
            _ = ShowSplitErrorAsync("Нельзя добавить домены или ресурсы obxodka в список исключений.");
            return;
        }

        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length < 3 || (!normalized.Contains('.') && !IPAddress.TryParse(normalized, out _)))
        {
            _ = ShowSplitErrorAsync("Введите корректный домен (например, mybank.ru) или IPv4.");
            return;
        }

        ThemeAudioService.PlayClick();
        var current = Platforms.Windows.AppManager.GetCustomRules();
        if (!current.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            current.Add(normalized);
            Platforms.Windows.AppManager.SaveCustomRules(current);
        }

        CustomRuleEntry.Text = string.Empty;
        RenderCustomRulesChips();
        UpdateWindowsDynamicRouteCounter();
#endif
    }

#if WINDOWS
    private void RenderCustomRulesChips()
    {
        CustomRulesFlexLayout.Children.Clear();
        var rules = Platforms.Windows.AppManager.GetCustomRules();

        if (rules.Count == 0)
        {
            CustomRulesEmptyLabel.IsVisible = true;
            CustomRulesFlexLayout.IsVisible = false;
            return;
        }

        CustomRulesEmptyLabel.IsVisible = false;
        CustomRulesFlexLayout.IsVisible = true;

        foreach (var rule in rules)
        {
            var chip = new Border
            {
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(10, 6, 8, 6),
                BackgroundColor = ActiveChipBg,
                Stroke = ActiveChipColor,
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 14 }
            };

            var row = new HorizontalStackLayout
            {
                Spacing = 6,
                VerticalOptions = LayoutOptions.Center
            };

            var isIp = IPAddress.TryParse(rule, out _);
            row.Children.Add(new MauiIcon
            {
                Icon = isIp ? FluentIcons.Server20 : FluentIcons.Globe16,
                IconColor = ActiveChipBright,
                IconSize = 14,
                VerticalOptions = LayoutOptions.Center
            });

            var lbl = new Label
            {
                Text = rule,
                FontSize = 12,
                TextColor = ActiveChipBright,
                VerticalOptions = LayoutOptions.Center
            };
            lbl.SetDynamicResource(Label.FontFamilyProperty, "AppFontMedium");
            row.Children.Add(lbl);

            var removeBtn = new Border
            {
                Padding = new Thickness(2),
                BackgroundColor = Colors.Transparent,
                StrokeThickness = 0,
                VerticalOptions = LayoutOptions.Center,
                Content = new MauiIcon
                {
                    Icon = FluentIcons.Dismiss16,
                    IconColor = ErrorColor,
                    IconSize = 14
                }
            };

            var targetRule = rule;
            removeBtn.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() =>
                {
                    ThemeAudioService.PlayClick();
                    var list = Platforms.Windows.AppManager.GetCustomRules();
                    _ = list.RemoveAll(r => r.Equals(targetRule, StringComparison.OrdinalIgnoreCase));
                    Platforms.Windows.AppManager.SaveCustomRules(list);
                    RenderCustomRulesChips();
                    UpdateWindowsDynamicRouteCounter();
                })
            });

            row.Children.Add(removeBtn);
            chip.Content = row;
            CustomRulesFlexLayout.Children.Add(chip);
        }
    }
#endif

    private async void OnAddCustomExeClickedAsync(object? sender, EventArgs e)
    {
#if WINDOWS
        try
        {
            ThemeAudioService.PlayClick();
            var pickedPath = Platforms.Windows.AppManager.PickExecutableFile();
            if (string.IsNullOrWhiteSpace(pickedPath))
            {
                return;
            }

            if (Platforms.Windows.AppManager.IsSelfOrProtectedApp(pickedPath))
            {
                await ShowSplitErrorAsync("Нельзя добавить приложение obxodka в список исключений.");
                return;
            }

            if (_appManager is Platforms.Windows.AppManager winAppManager)
            {
                var addedItem = winAppManager.AddCustomExecutable(pickedPath);
                if (addedItem is not null)
                {
                    var existing = _allApps.FirstOrDefault(a => a.PackageName.Equals(addedItem.PackageName, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null)
                    {
                        _isUpdatingInternally = true;
                        existing.IsBypassed = true;
                        _isUpdatingInternally = false;
                        if (!string.IsNullOrEmpty(addedItem.IconPath))
                        {
                            existing.IconPath = addedItem.IconPath;
                        }
                        SyncSingleAppChip(existing);
                    }
                    else
                    {
                        _allApps.Insert(0, addedItem);
                        SyncSingleAppChip(addedItem);
                    }

                    UpdateCountersOnly();
                    ApplyFilterAndRenderList();
                    UpdateWindowsDynamicRouteCounter();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SPLIT PICK EXE ERROR] {ex.Message}");
            await ShowSplitErrorAsync("Не удалось добавить .exe файл.");
        }
#else
#pragma warning disable IDE0022
        await Task.CompletedTask;
#pragma warning restore IDE0022
#endif
    }

    private void ApplyFilterAndRenderList()
    {
        var q = SplitSearchEntry.Text?.Trim();
        ClearSearchBtn.IsVisible = !string.IsNullOrEmpty(q);

        IEnumerable<AppInfoItem> filtered = _allApps;
        if (_showOnlyBypassed)
        {
            filtered = filtered.Where(a => a.IsBypassed);
        }

        if (!string.IsNullOrEmpty(q))
        {
            filtered = filtered.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                           a.PackageName.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        _isUpdatingInternally = true;
        SplitAppsList.ItemsSource = filtered.ToList();
        _isUpdatingInternally = false;
    }

    private void UpdateCountersOnly()
    {
        var bypassedCount = 0;
        for (var i = 0; i < _allApps.Count; i++)
        {
            if (_allApps[i].IsBypassed)
            {
                bypassedCount++;
            }
        }

        FilterAllLabel.Text = $"Все ({_allApps.Count})";
        FilterBypassedLabel.Text = $"В обходе ({bypassedCount})";

        if (bypassedCount == 0)
        {
            ActiveBypassedSection.IsVisible = false;
        }
        else
        {
            ActiveBypassedSection.IsVisible = true;
            ActiveBypassedCountLabel.Text = $"{bypassedCount} прил.";
        }
    }

    private void RebuildAllBypassedChips()
    {
        _activeChipBorders.Clear();
        ActiveBypassedChipsLayout.Children.Clear();

        foreach (var app in _allApps)
        {
            if (app.IsBypassed)
            {
                var chip = CreateAppChipBorder(app);
                _activeChipBorders[app.PackageName] = chip;
                ActiveBypassedChipsLayout.Children.Add(chip);
            }
        }
    }

    private void SyncSingleAppChip(AppInfoItem app)
    {
        if (app.IsBypassed)
        {
            if (_activeChipBorders.ContainsKey(app.PackageName))
            {
                return;
            }

            var chip = CreateAppChipBorder(app);
            _activeChipBorders[app.PackageName] = chip;
            chip.Opacity = 0;
            chip.Scale = 0.85;
            ActiveBypassedChipsLayout.Children.Insert(0, chip);
            _ = chip.FadeToAsync(1.0, 150, Easing.CubicOut);
            _ = chip.ScaleToAsync(1.0, 180, Easing.SpringOut);
        }
        else if (_activeChipBorders.Remove(app.PackageName, out var existingChip))
        {
            _ = ActiveBypassedChipsLayout.Children.Remove(existingChip);
        }
    }

    private Border CreateAppChipBorder(AppInfoItem app)
    {
        var chip = new Border
        {
            Padding = new Thickness(10, 5, 8, 5),
            BackgroundColor = ActiveChipBg,
            Stroke = ActiveChipColor,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 14 }
        };

        var row = new HorizontalStackLayout
        {
            Spacing = 6,
            VerticalOptions = LayoutOptions.Center,
            InputTransparent = true
        };

        var nameLabel = new Label
        {
            Text = app.Name,
            FontSize = 12,
            TextColor = ActiveChipBright,
            VerticalOptions = LayoutOptions.Center,
            InputTransparent = true
        };
        nameLabel.SetDynamicResource(Label.FontFamilyProperty, "AppFontMedium");

        var closeLabel = new Label
        {
            Text = "✕",
            FontSize = 11,
            TextColor = ErrorColor,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(2, 0, 0, 0),
            InputTransparent = true
        };
        closeLabel.SetDynamicResource(Label.FontFamilyProperty, "AppFontBold");

        row.Children.Add(nameLabel);
        row.Children.Add(closeLabel);
        chip.Content = row;

        var targetApp = app;
        chip.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () => await RemoveFromBypassAsync(targetApp))
        });

        return chip;
    }

    private async Task RemoveFromBypassAsync(AppInfoItem app)
    {
        if (!IsSplitEditingAllowed)
        {
            await _parent.DisplayAlertAsync("Внимание", "Отключите VPN, чтобы изменить список приложений.", "OK");
            return;
        }

        ThemeAudioService.PlayClick();
        _isUpdatingInternally = true;
        app.IsBypassed = false;
        _isUpdatingInternally = false;
        SyncSingleAppChip(app);
        UpdateCountersOnly();
        if (_showOnlyBypassed)
        {
            ApplyFilterAndRenderList();
        }
        ScheduleSaveBypassedState();
    }

    private void ScheduleSaveBypassedState()
    {
        _saveDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _saveDebounceCts = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(180, token).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                var bypassed = _allApps.Where(a => a.IsBypassed).Select(a => a.PackageName).ToList();
                _appManager.SaveBypassedPackages(bypassed);
#if WINDOWS
                MainThread.BeginInvokeOnMainThread(UpdateWindowsDynamicRouteCounter);
#endif
            }
            catch { }
        }, token);
    }

    private void OnSplitSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        ClearSearchBtn.IsVisible = !string.IsNullOrEmpty(e.NewTextValue);
        _searchDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(110, token).ConfigureAwait(false);
                if (!token.IsCancellationRequested)
                {
                    MainThread.BeginInvokeOnMainThread(ApplyFilterAndRenderList);
                }
            }
            catch { }
        }, token);
    }

    private void OnClearSearchClicked(object? sender, EventArgs e)
    {
        _searchDebounceCts?.Cancel();
        SplitSearchEntry.Text = string.Empty;
        ApplyFilterAndRenderList();
    }

    private void OnFilterAllClicked(object? sender, EventArgs e)
    {
        ThemeAudioService.PlayClick();
        _ = ChipFilterAll.BounceClickAsync();
        _showOnlyBypassed = false;
        _allApps =
        [
            .. _allApps
                .OrderByDescending(a => a.IsBypassed)
                .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
        ];
        UpdateFilterChipStyles();
        ApplyFilterAndRenderList();
        SplitAppsList.Opacity = 0.35;
        SplitAppsList.TranslationY = 10;
        _ = SplitAppsList.FadeToAsync(1.0, 160, Easing.CubicOut);
        _ = SplitAppsList.TranslateToAsync(0, 0, 160, Easing.CubicOut);
    }

    private void OnFilterBypassedClicked(object? sender, EventArgs e)
    {
        ThemeAudioService.PlayClick();
        _ = ChipFilterBypassed.BounceClickAsync();
        _showOnlyBypassed = true;
        UpdateFilterChipStyles();
        ApplyFilterAndRenderList();
        SplitAppsList.Opacity = 0.35;
        SplitAppsList.TranslationY = 10;
        _ = SplitAppsList.FadeToAsync(1.0, 160, Easing.CubicOut);
        _ = SplitAppsList.TranslateToAsync(0, 0, 160, Easing.CubicOut);
    }

    private void UpdateFilterChipStyles()
    {
        if (_showOnlyBypassed)
        {
            ChipFilterAll.BackgroundColor = InactiveChipBg;
            ChipFilterAll.Stroke = InactiveChipStroke;
            FilterAllLabel.TextColor = InactiveChipText;

            ChipFilterBypassed.BackgroundColor = ActiveChipBg;
            ChipFilterBypassed.Stroke = ActiveChipColor;
            FilterBypassedLabel.TextColor = ActiveChipBright;
        }
        else
        {
            ChipFilterAll.BackgroundColor = ActiveChipBg;
            ChipFilterAll.Stroke = ActiveChipColor;
            FilterAllLabel.TextColor = ActiveChipBright;

            ChipFilterBypassed.BackgroundColor = InactiveChipBg;
            ChipFilterBypassed.Stroke = InactiveChipStroke;
            FilterBypassedLabel.TextColor = InactiveChipText;
        }
    }

    public void UpdateCardOpacity()
    {
        var bgSurface = (Application.Current?.Resources.TryGetValue("BgSurface", out var bg) == true && bg is Color bgColor)
            ? bgColor
            : Color.FromArgb("#161622");

        HeaderContainer.BackgroundColor = Colors.Transparent;
        ChipResetAll.BackgroundColor = bgSurface;
        ChipRefreshApps.BackgroundColor = bgSurface;
        UpdateFilterChipStyles();
        UpdateModeTabStyles();
    }

    public void SetHeaderTopInset(double top)
    {
        if (DeviceInfo.Platform == DevicePlatform.Android && DeviceInfo.Idiom == DeviceIdiom.Phone)
        {
            var safeTop = Math.Max(top + 16, 50);
            HeaderContainer.Padding = new Thickness(0, safeTop, 0, 6);
        }
        else if (DeviceInfo.Idiom == DeviceIdiom.Phone)
        {
            var safeTop = Math.Max(top, 10);
            HeaderContainer.Padding = new Thickness(0, safeTop, 0, 6);
        }
    }

    public void OnThemeChanged() =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateCardOpacity();
            UpdateFilterChipStyles();
            UpdateModeTabStyles();
            RebuildAllBypassedChips();
            UpdateCountersOnly();
#if WINDOWS
            RenderCustomRulesChips();
            RenderActiveBypassRoutesChips();
#endif
        });

    private async void OnResetAllBypassedClickedAsync(object? sender, EventArgs e)
    {
        _ = ChipResetAll.BounceClickAsync();
        if (!IsSplitEditingAllowed)
        {
            await _parent.DisplayAlertAsync("Внимание", "Отключите VPN, чтобы изменить список приложений.", "OK");
            return;
        }

        var confirmed = await _parent.DisplayAlertAsync("Сброс", "Сбросить все правила обхода VPN для приложений?", "Сбросить", "Отмена");
        if (!confirmed)
        {
            return;
        }

        ThemeAudioService.PlayClick();
        _isUpdatingInternally = true;
        foreach (var app in _allApps)
        {
            app.IsBypassed = false;
        }
        _isUpdatingInternally = false;

        _activeChipBorders.Clear();
        ActiveBypassedChipsLayout.Children.Clear();
        UpdateCountersOnly();
        if (_showOnlyBypassed)
        {
            _showOnlyBypassed = false;
            UpdateFilterChipStyles();
        }
        ApplyFilterAndRenderList();
        ScheduleSaveBypassedState();
    }

    private void OnAppRowTapped(object? sender, EventArgs e)
    {
        if (sender is VisualElement { BindingContext: AppInfoItem app })
        {
            if (!IsSplitEditingAllowed)
            {
                return;
            }

            ThemeAudioService.PlayClick();
            _isUpdatingInternally = true;
            app.IsBypassed = !app.IsBypassed;
            _isUpdatingInternally = false;

            SyncSingleAppChip(app);
            UpdateCountersOnly();
            if (_showOnlyBypassed && !app.IsBypassed)
            {
                ApplyFilterAndRenderList();
            }
            ScheduleSaveBypassedState();
        }
    }

    private CancellationTokenSource? _toastCts;

    private async Task ShowSplitErrorAsync(string msg)
    {
        _toastCts?.Cancel();
        _toastCts = new CancellationTokenSource();
        var token = _toastCts.Token;

        SplitToastText.Text = msg;
        SplitToastNotification.Opacity = 0;
        SplitToastNotification.TranslationY = 20;
        SplitToastNotification.IsVisible = true;

        var fadeTask = SplitToastNotification.FadeToAsync(1, 180, Easing.CubicOut);
        var translateTask = SplitToastNotification.TranslateToAsync(0, 0, 180, Easing.CubicOut);
        _ = await Task.WhenAll(fadeTask, translateTask).ConfigureAwait(true);

        try
        {
            await Task.Delay(4000, token);
            if (!token.IsCancellationRequested)
            {
                await HideSplitToastAsync();
            }
        }
        catch (TaskCanceledException)
        {
        }
    }

    private async Task HideSplitToastAsync()
    {
        if (!SplitToastNotification.IsVisible)
        {
            return;
        }

        var fadeTask = SplitToastNotification.FadeToAsync(0, 150, Easing.CubicIn);
        var translateTask = SplitToastNotification.TranslateToAsync(0, 20, 150, Easing.CubicIn);
        _ = await Task.WhenAll(fadeTask, translateTask).ConfigureAwait(true);
        SplitToastNotification.IsVisible = false;
    }

    private void OnDismissToastClicked(object? sender, EventArgs e)
    {
        _toastCts?.Cancel();
        _ = HideSplitToastAsync();
    }

    private void UpdateRestartWarningVisibility(AppVpnState s)
    {
#if WINDOWS
        _ = s;
        SplitRestartWarningCard.IsVisible = false;
#else
        SplitRestartWarningCard.IsVisible = s == AppVpnState.Connected;
#endif
    }
}
