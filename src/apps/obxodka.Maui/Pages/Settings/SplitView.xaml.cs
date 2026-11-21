namespace obxodka.Pages.Settings;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "ContentView lifecycle is managed by MAUI visual tree")]
public sealed partial class SplitView : IAdaptivePage
{
    public static readonly BindableProperty IsSplitEditingAllowedProperty =
        BindableProperty.Create(nameof(IsSplitEditingAllowed), typeof(bool), typeof(SplitView), false);

    private readonly Dictionary<string, Border> _activeChipBorders = [with(StringComparer.OrdinalIgnoreCase)];
    private List<AppInfoItem> _allApps = [];
    private IAppManager _appManager = null!;
    private CancellationTokenSource? _headerAnimCts;
    private bool _isHeaderHidden;
    private bool _isSitesTabActive;
    private double _lastAppsScrollY;
    private double _lastSitesScrollY;
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
        SplitAppsScrollView.Scrolled += OnSplitAppsScrollViewScrolled;
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
        UpdateRestartWarningVisibility(_parent.VpnService.CurrentState);
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        if (_saveDebounceCts is not null)
        {
            _saveDebounceCts.Cancel();
            _saveDebounceCts = null;
            try
            {
                var bypassed = _allApps.Where(a => a.IsBypassed).Select(a => a.PackageName).ToList();
                _appManager.SaveBypassedPackages(bypassed);
            }
            catch
            {
                // ignored
            }
        }

        _headerAnimCts?.Cancel();
        _toastCts?.Cancel();
        HeaderContainer.SizeChanged -= OnHeaderContainerSizeChanged;
        SitesSplitScrollView.Scrolled -= OnSitesScrollViewScrolled;
        SplitAppsScrollView.Scrolled -= OnSplitAppsScrollViewScrolled;
        _parent.VpnService.OnStateChanged -= OnVpnStateChanged;
#if WINDOWS
        _routesDebounceCts?.Cancel();
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            Platforms.Windows.WindowsSplitTunnelEngine.OnRoutesUpdated -= OnWindowsRoutesUpdated;
        }
#endif
    }

    private void OnVpnStateChanged(AppVpnState s) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateRestartWarningVisibility(s);
#if WINDOWS
            IsSplitEditingAllowed = true;
            UpdateWindowsDynamicRouteCounter();
#else
            if (_allApps.Count <= 0)
            {
                return;
            }

            IsSplitEditingAllowed = s == AppVpnState.Disconnected;
            AndroidWarningBanner.IsVisible = !IsSplitEditingAllowed;
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
        _toastCts?.Cancel();
        _ = HideSplitToastAsync();
        _isSitesTabActive = false;
        AppsHeaderControls.IsVisible = true;
        SplitAppsScrollView.IsVisible = true;
        SitesSplitScrollView.IsVisible = false;
        UpdateModeTabStyles();
        UpdateFilterChipStyles();
        ApplyFilterAndRenderList();
        _ = UiAnimations.PlayEntranceCascadeAsync(30, 200, AppsHeaderControls, SplitAppsScrollView);
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
        _toastCts?.Cancel();
        _ = HideSplitToastAsync();
        _isSitesTabActive = true;
        AppsHeaderControls.IsVisible = false;
        SplitAppsScrollView.IsVisible = false;
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
        if (_toastCts is { } oldCts)
        {
            await oldCts.CancelAsync().ConfigureAwait(true);
        }

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
#if WINDOWS
    private bool _isUpdatingInternally;
    private CancellationTokenSource? _routesDebounceCts;
#endif
}
