namespace obxodka.Pages.Settings;

public sealed partial class SplitView
{
    public async Task LoadSplitAppsAsync(bool forceReload = false)
    {
        ApplySplitLockState(_parent.VpnService.CurrentState);
#if WINDOWS
        LoadWindowsSitesAndPresetsState();
#endif
        if (Interlocked.CompareExchange(ref _isLoadingApps, 1, 0) != 0)
        {
            return;
        }

        try
        {
            List<AppInfoItem> existingSnapshot;
            lock (_appsLock)
            {
                existingSnapshot = [.. _allApps];
            }

            if (!forceReload && existingSnapshot.Count > 0)
            {
                var updated = await Task.Run(() =>
                {
                    var bypassed = _appManager.GetBypassedPackages().ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var app in existingSnapshot)
                    {
                        app.IsBypassed = bypassed.Contains(app.PackageName);
                    }

                    return existingSnapshot
                        .Where(a => !SplitTunnelPolicy.IsProtectedObxodkaEntry(a.Name) &&
                                    !SplitTunnelPolicy.IsProtectedObxodkaEntry(a.PackageName))
                        .OrderByDescending(a => a.IsBypassed)
                        .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToList();
                });

                lock (_appsLock)
                {
                    _allApps = updated;
                }

                UpdateFilterChipStyles();
                RebuildAllBypassedChips();
                UpdateCountersOnly();
                ApplyFilterAndRenderList();
                ApplySplitLockState(_parent.VpnService.CurrentState);
                return;
            }

            SplitLoadingOverlay.IsVisible = true;
            if (!_isSitesTabActive)
            {
                SplitAppsCollectionView.IsVisible = false;
            }

            _ = HideSplitToastAsync();

            try
            {
                var apps = await _appManager.GetInstalledAppsAsync();
                var sorted = await Task.Run(() =>
                    apps
                        .Where(a => !SplitTunnelPolicy.IsProtectedObxodkaEntry(a.Name) &&
                                    !SplitTunnelPolicy.IsProtectedObxodkaEntry(a.PackageName))
                        .OrderByDescending(a => a.IsBypassed)
                        .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToList());

                lock (_appsLock)
                {
                    _allApps = sorted;
                }

                UpdateFilterChipStyles();
                RebuildAllBypassedChips();
                UpdateCountersOnly();
                ApplyFilterAndRenderList();
                ApplySplitLockState(_parent.VpnService.CurrentState);
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"[SPLIT ERROR] {ex.Message}", ex);
                await ShowSplitErrorAsync(SettingsMessages.LoadAppsFailed);
                ApplySplitLockState(_parent.VpnService.CurrentState);
            }
            finally
            {
                SplitLoadingOverlay.IsVisible = false;
                if (!_isSitesTabActive)
                {
                    SplitAppsCollectionView.IsVisible = true;
                }
            }
        }
        finally
        {
            _ = Interlocked.Exchange(ref _isLoadingApps, 0);
        }
    }

    private async void OnRefreshAppsClickedAsync(object? sender, EventArgs e)
    {
        try
        {
            if (Volatile.Read(ref _isLoadingApps) != 0)
            {
                return;
            }

            ThemeAudioService.PlayClick();
            _ = ChipRefreshApps.BounceClickAsync();
            _ = Interlocked.Increment(ref _filterGeneration);
            _activeChipBorders.Clear();
            ActiveBypassedChipsLayout.Children.Clear();
            SplitAppsCollectionView.ItemsSource = null;
            await LoadSplitAppsAsync(forceReload: true);
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
            if (!IsSplitEditingAllowed)
            {
                await _parent.DisplayAlertAsync(AccountMessages.AttentionTitle, SettingsMessages.VpnDisconnectRequired,
                    AccountMessages.ButtonOk);
                return;
            }

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

            AppInfoItem? existing;
            lock (_appsLock)
            {
                existing = _allApps.FirstOrDefault(a => a.PackageName.Equals(addedItem.PackageName, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    _allApps.Insert(0, addedItem);
                }
            }

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

    //виртуализированная фильтрация списка приложений в фоне без блокировки ui-потока
    private void ApplyFilterAndRenderList(bool resort = false)
    {
        var q = SplitSearchEntry.Text?.Trim() ?? string.Empty;
        ClearSearchBtn.IsVisible = !string.IsNullOrEmpty(q);

        List<AppInfoItem> snapshot;
        lock (_appsLock)
        {
            snapshot = [.. _allApps];
        }

        var onlyBypassed = _showOnlyBypassed;
        var generation = Interlocked.Increment(ref _filterGeneration);

        if (snapshot.Count <= 30 && !resort)
        {
            SplitAppsCollectionView.ItemsSource = FilterAppsSnapshot(snapshot, onlyBypassed, q, false);
            return;
        }

        _ = Task.Run(() =>
        {
            var result = FilterAppsSnapshot(snapshot, onlyBypassed, q, resort);
            if (resort)
            {
                lock (_appsLock)
                {
                    if (_allApps.Count == snapshot.Count)
                    {
                        _allApps = [.. snapshot
                            .OrderByDescending(a => a.IsBypassed)
                            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
                    }
                }
            }

            if (generation != Volatile.Read(ref _filterGeneration))
            {
                return;
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (generation == Volatile.Read(ref _filterGeneration))
                {
                    SplitAppsCollectionView.ItemsSource = result;
                }
            });
        });
    }

    private static List<AppInfoItem> FilterAppsSnapshot(List<AppInfoItem> source, bool onlyBypassed, string query, bool resort)
    {
        IEnumerable<AppInfoItem> filtered = source;
        if (onlyBypassed)
        {
            filtered = filtered.Where(a => a.IsBypassed);
        }

        if (!string.IsNullOrEmpty(query))
        {
            filtered = filtered.Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                           a.PackageName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        if (resort)
        {
            filtered = filtered
                .OrderByDescending(a => a.IsBypassed)
                .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase);
        }

        return [.. filtered];
    }

    private void UpdateCountersOnly()
    {
        int totalCount;
        int bypassedCount;
        lock (_appsLock)
        {
            totalCount = _allApps.Count;
            bypassedCount = _allApps.Count(t => t.IsBypassed);
        }

        FilterAllLabel.Text = $"Все ({totalCount})";
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

        List<AppInfoItem> bypassedItems;
        lock (_appsLock)
        {
            bypassedItems = [.. _allApps.Where(a => a.IsBypassed)];
        }

        foreach (var app in bypassedItems)
        {
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
        var token = ReplaceCtsToken(ref _saveDebounceCts);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(180, token).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                List<string> bypassed;
                lock (_appsLock)
                {
                    bypassed = [.. _allApps.Where(a => a.IsBypassed).Select(a => a.PackageName)];
                }

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
        var token = ReplaceCtsToken(ref _searchDebounceCts);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(120, token).ConfigureAwait(false);
                if (!token.IsCancellationRequested)
                {
                    MainThread.BeginInvokeOnMainThread(() => ApplyFilterAndRenderList());
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
        CancelAndDisposeCts(ref _searchDebounceCts);
        SplitSearchEntry.Text = string.Empty;
        ApplyFilterAndRenderList();
    }

    private void OnFilterAllClicked(object? sender, EventArgs e)
    {
        if (!_showOnlyBypassed)
        {
            return;
        }

        ThemeAudioService.PlayClick();
        _ = ChipFilterAll.BounceClickAsync();
        _showOnlyBypassed = false;
        UpdateFilterChipStyles();
        ApplyFilterAndRenderList(resort: true);
    }

    private void OnFilterBypassedClicked(object? sender, EventArgs e)
    {
        if (_showOnlyBypassed)
        {
            return;
        }

        ThemeAudioService.PlayClick();
        _ = ChipFilterBypassed.BounceClickAsync();
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
            lock (_appsLock)
            {
                foreach (var app in _allApps)
                {
                    app.IsBypassed = false;
                }
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
