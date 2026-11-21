namespace obxodka.Pages.Settings;

public sealed partial class SplitView
{
    //единая адаптация экрана разделения трафика по ssot-снимку геометрии окна без widthrequest
    public void ApplyViewport(AdaptiveViewport viewport)
    {
        if (RootLayout is null || HeaderContainer is null)
        {
            return;
        }

        var isCompact = viewport.IsCompact;
        var baseSidePad = isCompact ? 16.0 : viewport.IsExpanded ? 28.0 : 20.0;
        var sidePad = viewport.ComputeHorizontalPadding(baseSidePad, 960.0);
        RootLayout.Padding = new Thickness(sidePad, 0);

        var safeTop = DeviceInfo.Platform == DevicePlatform.Android || DeviceInfo.Platform == DevicePlatform.iOS
            ? Math.Max(viewport.SafeTop + 10.0, isCompact ? 38.0 : 22.0)
            : isCompact ? 14.0 : 22.0;
        HeaderContainer.Margin = new Thickness(0, safeTop, 0, 0);

        UpdateTopSpacers();
    }

    private void OnHeaderContainerSizeChanged(object? sender, EventArgs e) => UpdateTopSpacers();

    private void UpdateTopSpacers()
    {
        if (HeaderContainer is null)
        {
            return;
        }

        var topMargin = HeaderContainer.Margin.Top;
        var spacerH = HeaderContainer.Height > 0
            ? HeaderContainer.Height + topMargin + 10.0
            : topMargin + 210.0;

        if (AppsListTopSpacer is not null && Math.Abs(AppsListTopSpacer.HeightRequest - spacerH) > 1.0)
        {
            AppsListTopSpacer.HeightRequest = spacerH;
        }

        if (SitesListTopSpacer is not null && Math.Abs(SitesListTopSpacer.HeightRequest - spacerH) > 1.0)
        {
            SitesListTopSpacer.HeightRequest = spacerH;
        }
    }

    private void OnSplitAppsScrollViewScrolled(object? sender, ScrolledEventArgs e)
    {
        if (SplitAppsScrollView.Height <= 0)
        {
            return;
        }

        var currentScrollY = e.ScrollY;
        var deltaY = currentScrollY - _lastAppsScrollY;
        _lastAppsScrollY = currentScrollY;
        UpdateQuickReturnHeader(currentScrollY, deltaY);
    }

    private void OnSitesScrollViewScrolled(object? sender, ScrolledEventArgs e)
    {
        if (SitesSplitScrollView.Height <= 0)
        {
            return;
        }

        var currentScrollY = e.ScrollY;
        var deltaY = currentScrollY - _lastSitesScrollY;
        _lastSitesScrollY = currentScrollY;
        UpdateQuickReturnHeader(currentScrollY, deltaY);
    }

    private void UpdateQuickReturnHeader(double scrollY, double deltaY)
    {
        if (HeaderContainer is null || HeaderContainer.Height <= 0 || SplitSearchEntry is not null && SplitSearchEntry.IsFocused)
        {
            return;
        }

        var maxHideOffset = HeaderContainer.Height + HeaderContainer.Margin.Top + 20.0;

        if (scrollY <= 25.0)
        {
            if (!_isHeaderHidden && !(Math.Abs(HeaderContainer.TranslationY) > 0.5))
            {
                return;
            }

            _isHeaderHidden = false;
            _headerAnimCts?.Cancel();
            _headerAnimCts = new CancellationTokenSource();
            _ = AnimateHeaderAsync(0, 1.0, false);

            return;
        }

        switch (deltaY)
        {
            case > 12.0 when !_isHeaderHidden && scrollY > 70.0:
                _isHeaderHidden = true;
                _headerAnimCts?.Cancel();
                _headerAnimCts = new CancellationTokenSource();
                _ = AnimateHeaderAsync(-maxHideOffset, 0.0, true);
                break;
            case < -10.0 when _isHeaderHidden:
                _isHeaderHidden = false;
                _headerAnimCts?.Cancel();
                _headerAnimCts = new CancellationTokenSource();
                _ = AnimateHeaderAsync(0, 1.0, false);
                break;
        }
    }

    private async Task AnimateHeaderAsync(double targetY, double targetOpacity, bool inputTransparent)
    {
        try
        {
            HeaderContainer.InputTransparent = inputTransparent;
            var dist = Math.Abs(HeaderContainer.TranslationY - targetY);
            var duration = (uint)Math.Clamp(dist * 0.8, 140, 240);
            var tTask = HeaderContainer.TranslateToAsync(0, targetY, duration, Easing.CubicOut);
            var fTask = HeaderContainer.FadeToAsync(targetOpacity, duration, Easing.CubicOut);
            _ = await Task.WhenAll(tTask, fTask).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ResetHeaderToVisible()
    {
        _isHeaderHidden = false;
        _headerAnimCts?.Cancel();
        if (HeaderContainer is not null)
        {
            HeaderContainer.TranslationY = 0;
            HeaderContainer.Opacity = 1.0;
            HeaderContainer.InputTransparent = false;
        }

        _lastAppsScrollY = 0;
        _lastSitesScrollY = 0;
    }

    public async Task PlayEntranceAnimationAsync()
    {
#if WINDOWS
        LoadWindowsSitesAndPresetsState();
#endif
        ResetHeaderToVisible();
        Opacity = 1;
        TranslationY = 0;
        if (_isSitesTabActive)
        {
            await UiAnimations.PlayEntranceCascadeAsync(
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
            await UiAnimations.PlayEntranceCascadeAsync(35, 220, HeaderContainer, SplitAppsScrollView);
        }
    }

    public void UpdateCardOpacity()
    {
        var bgSurface = Application.Current?.Resources.TryGetValue("BgSurface", out var bg) == true && bg is Color bgColor
            ? bgColor
            : Color.FromArgb("#161622");
        var borderSubtle = Application.Current?.Resources.TryGetValue("BorderSubtle", out var bs) == true && bs is Color bsc
            ? bsc
            : Color.FromArgb("#282838");

        HeaderContainer.BackgroundColor = bgSurface;
        HeaderContainer.Stroke = borderSubtle;
        ChipResetAll.BackgroundColor = InactiveChipBg;
        ChipRefreshApps.BackgroundColor = InactiveChipBg;
        UpdateFilterChipStyles();
        UpdateModeTabStyles();
    }
}
