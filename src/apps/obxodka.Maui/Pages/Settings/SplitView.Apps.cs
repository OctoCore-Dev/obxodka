namespace obxodka.Pages.Settings;

public sealed partial class SplitView
{
    public async Task LoadSplitAppsAsync()
    {
        UpdateRestartWarningVisibility(_parent.VpnService.CurrentState);
#if WINDOWS
        LoadWindowsSitesAndPresetsState();
#endif
        if (_allApps.Count > 0)
        {
            var bypassed = _appManager.GetBypassedPackages().ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var app in _allApps)
            {
                app.IsBypassed = bypassed.Contains(app.PackageName);
            }

            _allApps =
            [
                .. _allApps
                    .Where(a => !SplitTunnelPolicy.IsProtectedObxodkaEntry(a.Name) &&
                                !SplitTunnelPolicy.IsProtectedObxodkaEntry(a.PackageName))
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
            SplitAppsScrollView.IsVisible = false;
        }

        _ = HideSplitToastAsync();

        try
        {
            var apps = await _appManager.GetInstalledAppsAsync();
            _allApps =
            [
                .. apps
                    .Where(a => !SplitTunnelPolicy.IsProtectedObxodkaEntry(a.Name) &&
                                !SplitTunnelPolicy.IsProtectedObxodkaEntry(a.PackageName))
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
        catch (Exception ex)
        {
            AppLogger.LogError($"[SPLIT ERROR] {ex.Message}", ex);
            await ShowSplitErrorAsync(SettingsMessages.LoadAppsFailed);
#if !WINDOWS
            IsSplitEditingAllowed = false;
#endif
        }
        finally
        {
            SplitLoadingOverlay.IsVisible = false;
            if (!_isSitesTabActive)
            {
                SplitAppsScrollView.IsVisible = true;
            }
        }
    }

    private async void OnRefreshAppsClickedAsync(object? sender, EventArgs e)
    {
        try
        {
            ThemeAudioService.PlayClick();
            _ = ChipRefreshApps.BounceClickAsync();
            _allApps.Clear();
            _activeChipBorders.Clear();
            ActiveBypassedChipsLayout.Children.Clear();
            BindableLayout.SetItemsSource(SplitAppsItemsStack, null);
            await LoadSplitAppsAsync();
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[SPLIT REFRESH ERROR] {ex.Message}", ex);
            await ShowSplitErrorAsync(SettingsMessages.LoadAppsFailed);
        }
    }

    private async void OnAddCustomExeClickedAsync(object? sender, EventArgs e)
    {
        try
        {
#if WINDOWS
            ThemeAudioService.PlayClick();
            var pickedPath = AppManager.PickExecutableFile();
            if (string.IsNullOrWhiteSpace(pickedPath))
            {
                return;
            }

            if (AppManager.IsSelfOrProtectedApp(pickedPath))
            {
                await ShowSplitErrorAsync("Нельзя добавить приложение obxodka в список исключений.");
                return;
            }

            if (_appManager is not AppManager winAppManager)
            {
                return;
            }

            var addedItem = winAppManager.AddCustomExecutable(pickedPath);
            if (addedItem is null)
            {
                return;
            }

            var existing =
                _allApps.FirstOrDefault(a => a.PackageName.Equals(addedItem.PackageName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.IsBypassed = true;
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
#else
            await Task.CompletedTask;
#endif
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[SPLIT PICK EXE ERROR] {ex.Message}", ex);
            _ = ShowSplitErrorAsync(SettingsMessages.AddExeFailed);
        }
    }

    private void ApplyFilterAndRenderList()
    {
        var q = SplitSearchEntry.Text?.Trim() ?? string.Empty;
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

        BindableLayout.SetItemsSource(SplitAppsItemsStack, filtered.ToList());
    }

    private void UpdateCountersOnly()
    {
        var bypassedCount = _allApps.Count(t => t.IsBypassed);

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
            if (!app.IsBypassed)
            {
                continue;
            }

            var chip = CreateAppChipBorder(app);
            _activeChipBorders[app.PackageName] = chip;
            ActiveBypassedChipsLayout.Children.Add(chip);
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
            StrokeShape = new RoundRectangle
            {
                CornerRadius = 14
            }
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
            Command = new Command(() => _ = RemoveFromBypassAsync(targetApp))
        });

        return chip;
    }

    private async Task RemoveFromBypassAsync(AppInfoItem app)
    {
        try
        {
            if (!IsSplitEditingAllowed)
            {
                await _parent.DisplayAlertAsync(AccountMessages.AttentionTitle, SettingsMessages.VpnDisconnectRequired,
                    AccountMessages.ButtonOk);
                return;
            }

            ThemeAudioService.PlayClick();
            app.IsBypassed = false;
            SyncSingleAppChip(app);
            UpdateCountersOnly();
            if (_showOnlyBypassed)
            {
                ApplyFilterAndRenderList();
            }

            ScheduleSaveBypassedState();
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[SPLIT REMOVE APP ERROR] {ex.Message}", ex);
        }
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
            catch
            {
                // ignored
            }
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
            catch
            {
                // ignored
            }
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
        SplitAppsScrollView.Opacity = 0.35;
        SplitAppsScrollView.TranslationY = 10;
        _ = SplitAppsScrollView.FadeToAsync(1.0, 160, Easing.CubicOut);
        _ = SplitAppsScrollView.TranslateToAsync(0, 0, 160, Easing.CubicOut);
    }

    private void OnFilterBypassedClicked(object? sender, EventArgs e)
    {
        ThemeAudioService.PlayClick();
        _ = ChipFilterBypassed.BounceClickAsync();
        _showOnlyBypassed = true;
        UpdateFilterChipStyles();
        ApplyFilterAndRenderList();
        SplitAppsScrollView.Opacity = 0.35;
        SplitAppsScrollView.TranslationY = 10;
        _ = SplitAppsScrollView.FadeToAsync(1.0, 160, Easing.CubicOut);
        _ = SplitAppsScrollView.TranslateToAsync(0, 0, 160, Easing.CubicOut);
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

    private async void OnResetAllBypassedClickedAsync(object? sender, EventArgs e)
    {
        try
        {
            _ = ChipResetAll.BounceClickAsync();
            if (!IsSplitEditingAllowed)
            {
                await _parent.DisplayAlertAsync(AccountMessages.AttentionTitle, SettingsMessages.VpnDisconnectRequired,
                    AccountMessages.ButtonOk);
                return;
            }

            var confirmed = await _parent.DisplayAlertAsync(SettingsMessages.ResetRulesTitle, SettingsMessages.ResetRulesConfirm,
                SettingsMessages.ButtonReset, AccountMessages.ButtonCancel);
            if (!confirmed)
            {
                return;
            }

            ThemeAudioService.PlayClick();
            foreach (var app in _allApps)
            {
                app.IsBypassed = false;
            }

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
        catch (Exception ex)
        {
            AppLogger.LogError($"[SPLIT RESET ERROR] {ex.Message}", ex);
        }
    }

    private void OnAppRowTapped(object? sender, EventArgs e)
    {
        if (sender is not VisualElement { BindingContext: AppInfoItem app } || !IsSplitEditingAllowed)
        {
            return;
        }

        ThemeAudioService.PlayClick();
        app.IsBypassed = !app.IsBypassed;

        SyncSingleAppChip(app);
        UpdateCountersOnly();
        if (_showOnlyBypassed && !app.IsBypassed)
        {
            ApplyFilterAndRenderList();
        }

        ScheduleSaveBypassedState();
    }
}
