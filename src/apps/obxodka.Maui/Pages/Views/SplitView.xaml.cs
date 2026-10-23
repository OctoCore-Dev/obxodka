using Microsoft.Maui.Controls.Shapes;
using Switch = Microsoft.Maui.Controls.Switch;

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
    private IAppManager _appManager = null!;

    private List<AppInfoItem> _allApps = [];
    private bool _showOnlyBypassed;
    private bool _isSitesTabActive;
    private bool _isUpdatingInternally;

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
#if WINDOWS
        ModeTabsGrid.IsVisible = true;
        ChipAddExe.IsVisible = true;
        AndroidWarningBanner.IsVisible = false;
        IsSplitEditingAllowed = true;
        LoadWindowsSitesAndPresetsState();
#endif
    }

    public void Initialize(MainPage parent, IAppManager appManager)
    {
        _parent = parent;
        _appManager = appManager;
        _parent.VpnService.OnStateChanged += OnVpnStateChanged;
        UpdateRestartWarningVisibility(_parent.VpnService.CurrentState);
    }

    public void ForceLayoutWidth()
    {
        if (RootLayout is not null && RootLayout.Width > 0)
        {
            var avail = RootLayout.Width - RootLayout.Padding.HorizontalThickness;
            if (avail > 0)
            {
                ApplyCardWidth(avail);
            }
        }
        else if (Width > 0 && RootLayout is not null)
        {
            var avail = Width - RootLayout.Padding.HorizontalThickness;
            if (avail > 0)
            {
                ApplyCardWidth(avail);
            }
        }
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width > 0 && RootLayout is not null)
        {
            var availableWidth = width - RootLayout.Padding.HorizontalThickness;
            if (availableWidth > 0)
            {
                ApplyCardWidth(availableWidth);
            }
        }
    }

    private void OnRootLayoutSizeChanged(object? sender, EventArgs e)
    {
        if (RootLayout is not null && RootLayout.Width > 0)
        {
            var availableWidth = RootLayout.Width - RootLayout.Padding.HorizontalThickness;
            if (availableWidth > 0)
            {
                ApplyCardWidth(availableWidth);
            }
        }
    }

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
            return;
        }

        var safeWidth = Math.Min(targetWidth, 950);
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
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        RootLayout.SizeChanged -= OnRootLayoutSizeChanged;
        _parent?.VpnService.OnStateChanged -= OnVpnStateChanged;
    }

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
            }
#endif
        });

    public async Task LoadSplitAppsAsync()
    {
        UpdateRestartWarningVisibility(_parent.VpnService.CurrentState);
#if WINDOWS
        LoadWindowsSitesAndPresetsState();
#endif
        if (_allApps.Count > 0)
        {
            _allApps =
            [
                .. _allApps.Where(a => !Core.Models.SplitTunnelPolicy.IsProtectedObxodkaEntry(a.Name) &&
                                       !Core.Models.SplitTunnelPolicy.IsProtectedObxodkaEntry(a.PackageName))
            ];
            var bypassed = _appManager.GetBypassedPackages().ToHashSet(StringComparer.OrdinalIgnoreCase);
            _isUpdatingInternally = true;
            foreach (var app in _allApps)
            {
                app.IsBypassed = bypassed.Contains(app.PackageName);
            }
            _isUpdatingInternally = false;

            if (SplitAppsList.ItemsSource is null)
            {
                ApplyFilterAndRenderList();
            }

            UpdateChipsAndCounters();
#if WINDOWS
            IsSplitEditingAllowed = true;
#else
            IsSplitEditingAllowed = _parent.VpnService.CurrentState == AppVpnState.Disconnected;
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
                    .. apps.Where(a => !Core.Models.SplitTunnelPolicy.IsProtectedObxodkaEntry(a.Name) &&
                                       !Core.Models.SplitTunnelPolicy.IsProtectedObxodkaEntry(a.PackageName))
                ];
                ApplyFilterAndRenderList();
                UpdateChipsAndCounters();
#if WINDOWS
                IsSplitEditingAllowed = true;
#else
                IsSplitEditingAllowed = _parent.VpnService.CurrentState == AppVpnState.Disconnected;
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

    private void OnTabModeAppsClicked(object? sender, EventArgs e)
    {
        if (!_isSitesTabActive)
        {
            return;
        }

        ThemeAudioService.PlayClick();
        _toastCts?.Cancel();
        _ = HideSplitToastAsync();
        _isSitesTabActive = false;
        AppsHeaderControls.IsVisible = true;
        SplitAppsList.IsVisible = true;
        SitesSplitScrollView.IsVisible = false;
        UpdateModeTabStyles();
    }

    private void OnTabModeSitesClicked(object? sender, EventArgs e)
    {
        if (_isSitesTabActive)
        {
            return;
        }

        ThemeAudioService.PlayClick();
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
        TabModeSitesLabel.Text = rulesCount > 0 ? $"Сайты и Сервисы РФ ({rulesCount})" : "Сайты и Сервисы РФ";
        ActiveDynamicRoutesLabel.Text = count > 0 ? $"{count} маршр." : "Активно";
    }
#endif

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
        _ = Task.Run(async () =>
        {
            await Task.Delay(350);
            MainThread.BeginInvokeOnMainThread(UpdateWindowsDynamicRouteCounter);
        });
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
        _ = Task.Run(async () =>
        {
            await Task.Delay(350);
            MainThread.BeginInvokeOnMainThread(UpdateWindowsDynamicRouteCounter);
        });
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
                        existing.IsBypassed = true;
                        if (!string.IsNullOrEmpty(addedItem.IconPath))
                        {
                            existing.IconPath = addedItem.IconPath;
                        }
                    }
                    else
                    {
                        _allApps.Insert(0, addedItem);
                    }

                    ApplyFilterAndRenderList();
                    UpdateChipsAndCounters();
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
        var q = SplitSearchEntry.Text?.Trim().ToLowerInvariant();
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

    private void UpdateChipsAndCounters()
    {
        var bypassedApps = _allApps.Where(a => a.IsBypassed).ToList();
        FilterAllLabel.Text = $"Все ({_allApps.Count})";
        FilterBypassedLabel.Text = $"В обходе ({bypassedApps.Count})";

        if (bypassedApps.Count == 0)
        {
            ActiveBypassedSection.IsVisible = false;
        }
        else
        {
            ActiveBypassedSection.IsVisible = true;
            ActiveBypassedCountLabel.Text = $"{bypassedApps.Count} прил.";
            PopulateActiveBypassedChips(bypassedApps);
        }

        if (_showOnlyBypassed)
        {
            ApplyFilterAndRenderList();
        }
    }

    private void PopulateActiveBypassedChips(List<AppInfoItem> bypassedApps)
    {
        ActiveBypassedChipsLayout.Children.Clear();

        foreach (var app in bypassedApps)
        {
            var chip = new Border
            {
                Padding = new Thickness(10, 6, 8, 6),
                BackgroundColor = ActiveChipBg,
                Stroke = ActiveChipColor,
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 16 }
            };

            var grid = new Grid
            {
                ColumnDefinitions =
                [
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto)
                ],
                ColumnSpacing = 6
            };

            View iconView = app.HasIcon && app.IconPath is { Length: > 0 }
                ? new Image
                {
                    Source = ImageSource.FromFile(app.IconPath),
                    HeightRequest = 18,
                    WidthRequest = 18,
                    VerticalOptions = LayoutOptions.Center
                }
                : new MauiIcon
                {
                    Icon = FluentIcons.AppGeneric20,
                    IconColor = ActiveChipBright,
                    IconSize = 16,
                    VerticalOptions = LayoutOptions.Center
                };

            var nameLabel = new Label
            {
                Text = app.Name,
                FontSize = 12,
                TextColor = ActiveChipBright,
                VerticalOptions = LayoutOptions.Center
            };
            nameLabel.SetDynamicResource(Label.FontFamilyProperty, "AppFontMedium");

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

            var targetApp = app;
            removeBtn.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(async () => await RemoveFromBypassAsync(targetApp))
            });

            Grid.SetColumn(iconView, 0);
            Grid.SetColumn(nameLabel, 1);
            Grid.SetColumn(removeBtn, 2);

            grid.Children.Add(iconView);
            grid.Children.Add(nameLabel);
            grid.Children.Add(removeBtn);

            chip.Content = grid;
            ActiveBypassedChipsLayout.Children.Add(chip);
        }
    }

    private async Task RemoveFromBypassAsync(AppInfoItem app)
    {
        if (!IsSplitEditingAllowed)
        {
            await _parent.DisplayAlertAsync("Внимание", "Отключите VPN, чтобы изменить список приложений.", "OK");
            return;
        }

        ThemeAudioService.PlayClick();
        app.IsBypassed = false;
        SaveBypassedState();
        UpdateChipsAndCounters();
    }

    private void SaveBypassedState()
    {
        try
        {
            var bypassed = _allApps.Where(a => a.IsBypassed).Select(a => a.PackageName).ToList();
            _appManager.SaveBypassedPackages(bypassed);
#if WINDOWS
            UpdateWindowsDynamicRouteCounter();
#endif
        }
        catch { }
    }

    private void OnSplitSearchTextChanged(object? sender, TextChangedEventArgs e) => ApplyFilterAndRenderList();

    private void OnClearSearchClicked(object? sender, EventArgs e)
    {
        SplitSearchEntry.Text = string.Empty;
        ApplyFilterAndRenderList();
    }

    private void OnFilterAllClicked(object? sender, EventArgs e)
    {
        ThemeAudioService.PlayClick();
        _showOnlyBypassed = false;
        UpdateFilterChipStyles();
        ApplyFilterAndRenderList();
    }

    private void OnFilterBypassedClicked(object? sender, EventArgs e)
    {
        ThemeAudioService.PlayClick();
        _showOnlyBypassed = true;
        UpdateFilterChipStyles();
        ApplyFilterAndRenderList();
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
            UpdateChipsAndCounters();
#if WINDOWS
            RenderCustomRulesChips();
#endif
        });

    private async void OnResetAllBypassedClickedAsync(object? sender, EventArgs e)
    {
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

        _appManager.SaveBypassedPackages([]);
        UpdateChipsAndCounters();
#if WINDOWS
        UpdateWindowsDynamicRouteCounter();
#endif
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
            app.IsBypassed = !app.IsBypassed;
            SaveBypassedState();
            UpdateChipsAndCounters();
        }
    }

    private void OnSplitAppToggled(object? sender, ToggledEventArgs e)
    {
        if (_isUpdatingInternally || sender is not Switch { BindingContext: AppInfoItem app })
        {
            return;
        }

        if (!IsSplitEditingAllowed)
        {
            _isUpdatingInternally = true;
            app.IsBypassed = !e.Value;
            _isUpdatingInternally = false;
            return;
        }

        if (app.IsBypassed != e.Value)
        {
            app.IsBypassed = e.Value;
        }

        SaveBypassedState();
        UpdateChipsAndCounters();
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

    private void UpdateRestartWarningVisibility(AppVpnState s) =>
        SplitRestartWarningCard.IsVisible = s == AppVpnState.Connected;
}