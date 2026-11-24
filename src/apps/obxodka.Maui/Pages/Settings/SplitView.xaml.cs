namespace obxodka.Pages.Settings;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "ContentView lifecycle is managed by MAUI visual tree")]
public sealed partial class SplitView : IAdaptivePage
{
    public static readonly BindableProperty IsSplitEditingAllowedProperty =
        BindableProperty.Create(nameof(IsSplitEditingAllowed), typeof(bool), typeof(SplitView), false);

    private readonly Dictionary<string, Border> _activeChipBorders = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly Lock _appsLock = new();
    private List<AppInfoItem> _allApps = [];
    private IAppManager _appManager = null!;
    private int _filterGeneration;
    private int _isLoadingApps;
    private CancellationTokenSource? _headerAnimCts;
    private bool _isHeaderHidden;
    private bool _isSitesTabActive;
    private double _lastAppsScrollY;
    private double _lastSitesScrollY;
    private double _accumulatedScrollDelta;
#if WINDOWS
    private Action? _detachNativeAppsScroll;
#endif
    private MainPage _parent = null!;
    private CancellationTokenSource? _saveDebounceCts;
    private CancellationTokenSource? _searchDebounceCts;
    private bool _showOnlyBypassed;
    private CancellationTokenSource? _toastCts;

    public SplitView()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
        HeaderContainer.SizeChanged += OnHeaderContainerSizeChanged;
        SitesSplitScrollView.Scrolled += OnSitesScrollViewScrolled;
#if WINDOWS
        _detachNativeAppsScroll = Platforms.Windows.WinUiMainPageHelper.AttachNativeScrollViewer(
            SplitAppsCollectionView,
            OnNativeAppsScrollOffsetChanged);
        ModeTabsGrid.IsVisible = true;
        ChipAddExe.IsVisible = true;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            Platforms.Windows.WindowsSplitTunnelEngine.OnRoutesUpdated += OnWindowsRoutesUpdated;
        }

        LoadWindowsSitesAndPresetsState();
#endif
    }

    private static Color ActiveChipColor => Application.Current?.Resources.TryGetValue("Primary", out var val) == true && val is Color c
        ? c
        : Color.FromArgb("#7C3AED");

    private static Color ActiveChipBright =>
        Application.Current?.Resources.TryGetValue("PrimaryBright", out var val) == true && val is Color c
            ? c
            : ActiveChipColor;

    private static Color ActiveChipBg => Application.Current?.Resources.TryGetValue("PrimaryDim", out var pd) == true && pd is Color pdc
        ? pdc
        : ActiveChipColor.WithAlpha(0.18f);

    private static Color InactiveChipBg => Application.Current?.Resources.TryGetValue("BgElevated", out var bs) == true && bs is Color bsc
        ? bsc
        : Color.FromArgb("#202030");

    private static Color InactiveChipStroke =>
        Application.Current?.Resources.TryGetValue("BorderSubtle", out var bst) == true && bst is Color bstc
            ? bstc
            : Color.FromArgb("#30FFFFFF");

    private static Color InactiveChipText =>
        Application.Current?.Resources.TryGetValue("TextSecondary", out var ts) == true && ts is Color tsc
            ? tsc
            : Color.FromArgb("#9CA3AF");

    private static Color ErrorColor => Application.Current?.Resources.TryGetValue("Error", out var val) == true && val is Color c
        ? c
        : Color.FromArgb("#EF4444");

    public bool IsSplitEditingAllowed
    {
        get => (bool)GetValue(IsSplitEditingAllowedProperty);
        set => SetValue(IsSplitEditingAllowedProperty, value);
    }

    public void Initialize(MainPage parent, IAppManager appManager)
    {
        _parent = parent;
        _appManager = appManager;
        _parent.VpnService.OnStateChanged += OnVpnStateChanged;
        ApplySplitLockState(_parent.VpnService.CurrentState);
    }

    //безопасная замена и освобождение токена отмены без утечек
    private static CancellationToken ReplaceCtsToken(ref CancellationTokenSource? field)
    {
        var next = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref field, next);
        if (prev is not null)
        {
            try
            {
                prev.Cancel();
            }
            catch
            {
                // ignored
            }
            finally
            {
                prev.Dispose();
            }
        }

        return next.Token;
    }

    //отмена и освобождение токена отмены
    private static void CancelAndDisposeCts(ref CancellationTokenSource? field)
    {
        var prev = Interlocked.Exchange(ref field, null);
        if (prev is null)
        {
            return;
        }

        try
        {
            prev.Cancel();
        }
        catch
        {
            // ignored
        }
        finally
        {
            prev.Dispose();
        }
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _saveDebounceCts, null) is { } pendingSave)
        {
            try
            {
                pendingSave.Cancel();
            }
            catch
            {
                // ignored
            }
            finally
            {
                pendingSave.Dispose();
            }

            try
            {
                List<string> bypassed;
                lock (_appsLock)
                {
                    bypassed = [.. _allApps.Where(a => a.IsBypassed).Select(a => a.PackageName)];
                }

                _appManager?.SaveBypassedPackages(bypassed);
            }
            catch
            {
                // ignored
            }
        }

        _ = Interlocked.Increment(ref _filterGeneration);
        CancelAndDisposeCts(ref _headerAnimCts);
        CancelAndDisposeCts(ref _searchDebounceCts);
        CancelAndDisposeCts(ref _toastCts);
        HeaderContainer.SizeChanged -= OnHeaderContainerSizeChanged;
        SitesSplitScrollView.Scrolled -= OnSitesScrollViewScrolled;
        SplitAppsCollectionView.Scrolled -= OnSplitAppsCollectionScrolled;
        _parent?.VpnService.OnStateChanged -= OnVpnStateChanged;
#if WINDOWS
        _detachNativeAppsScroll?.Invoke();
        _detachNativeAppsScroll = null;
        CancelAndDisposeCts(ref _routesDebounceCts);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            Platforms.Windows.WindowsSplitTunnelEngine.OnRoutesUpdated -= OnWindowsRoutesUpdated;
        }
#endif
    }

    private void OnVpnStateChanged(AppVpnState s) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            ApplySplitLockState(s);
#if WINDOWS
            UpdateWindowsDynamicRouteCounter();
#endif
        });

    private void OnTabModeAppsClicked(object? sender, EventArgs e)
    {
        if (!_isSitesTabActive)
        {
            return;
        }

        ResetHeaderToVisible();
        ThemeAudioService.PlayClick();
        _ = TabModeApps.BounceClickAsync();
        CancelAndDisposeCts(ref _toastCts);
        _ = HideSplitToastAsync();
        _isSitesTabActive = false;
        AppsHeaderControls.IsVisible = true;
        SplitAppsCollectionView.IsVisible = true;
        SitesSplitScrollView.IsVisible = false;
        UpdateModeTabStyles();
        UpdateFilterChipStyles();
        ApplyFilterAndRenderList();
        _ = UiAnimations.PlayEntranceCascadeAsync(30, 200, AppsHeaderControls, SplitAppsCollectionView);
    }

    private void OnTabModeSitesClicked(object? sender, EventArgs e)
    {
        if (_isSitesTabActive)
        {
            return;
        }

        ResetHeaderToVisible();
        ThemeAudioService.PlayClick();
        _ = TabModeSites.BounceClickAsync();
        CancelAndDisposeCts(ref _toastCts);
        _ = HideSplitToastAsync();
        _isSitesTabActive = true;
        AppsHeaderControls.IsVisible = false;
        SplitAppsCollectionView.IsVisible = false;
        SitesSplitScrollView.IsVisible = true;
#if WINDOWS
        LoadWindowsSitesAndPresetsState();
#endif
        UpdateModeTabStyles();
        _ = UiAnimations.PlayEntranceCascadeAsync(
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

    private async Task ShowSplitErrorAsync(string msg)
    {
        var token = ReplaceCtsToken(ref _toastCts);

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
        CancelAndDisposeCts(ref _toastCts);
        _ = HideSplitToastAsync();
    }

    //единая блокировка редактирования правил раздельного туннелирования во время работы vpn (windows + android)
    private void ApplySplitLockState(AppVpnState state)
    {
        var editable = state is AppVpnState.Disconnected or AppVpnState.Error;
        IsSplitEditingAllowed = editable;
        VpnLockWarningBanner.IsVisible = !editable;

        var lockTip = editable ? null : "Отключите VPN для изменения";
        SplitAppsCollectionView.Opacity = editable ? 1.0 : 0.72;
        ChipResetAll.Opacity = editable ? 1.0 : 0.5;
        SetElementTooltip(ChipResetAll, lockTip);
        ChipAddExe.Opacity = editable ? 1.0 : 0.5;
        SetElementTooltip(ChipAddExe, lockTip);

#if WINDOWS
        var siteOpacity = editable ? 1.0 : 0.58;
        PresetGovSwitch.IsEnabled = editable;
        PresetBanksSwitch.IsEnabled = editable;
        PresetMediaSwitch.IsEnabled = editable;
        PresetMarketSwitch.IsEnabled = editable;
        PresetRuTldsSwitch.IsEnabled = editable;
        CustomRuleEntry.IsEnabled = editable;

        PresetGovCard.Opacity = siteOpacity;
        PresetBanksCard.Opacity = siteOpacity;
        PresetMediaCard.Opacity = siteOpacity;
        PresetMarketCard.Opacity = siteOpacity;
        PresetRuTldsCard.Opacity = siteOpacity;
        CustomRulesCard.Opacity = siteOpacity;

        SetElementTooltip(PresetGovCard, lockTip);
        SetElementTooltip(PresetBanksCard, lockTip);
        SetElementTooltip(PresetMediaCard, lockTip);
        SetElementTooltip(PresetMarketCard, lockTip);
        SetElementTooltip(PresetRuTldsCard, lockTip);
        SetElementTooltip(CustomRulesCard, lockTip);
#endif
    }

    private static void SetElementTooltip(BindableObject element, string? tooltip)
    {
        if (tooltip is null)
        {
            element.ClearValue(ToolTipProperties.TextProperty);
        }
        else
        {
            ToolTipProperties.SetText(element, tooltip);
        }
    }
#if WINDOWS
    private bool _isUpdatingInternally;
    private CancellationTokenSource? _routesDebounceCts;
#endif
}
