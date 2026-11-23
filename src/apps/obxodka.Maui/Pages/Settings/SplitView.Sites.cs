namespace obxodka.Pages.Settings;

public sealed partial class SplitView
{
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
        AppManager.SyncPolicyFromPreferences();
        UpdateWindowsDynamicRouteCounter();
#endif
    }

    private void OnPresetSwitchToggled(object? sender, ToggledEventArgs e)
    {
#if WINDOWS
        if (_isUpdatingInternally)
        {
            return;
        }

        ThemeAudioService.PlayClick();
        Preferences.Set(AppManager.PresetGovKey, PresetGovSwitch.IsToggled);
        Preferences.Set(AppManager.PresetBanksKey, PresetBanksSwitch.IsToggled);
        Preferences.Set(AppManager.PresetMediaKey, PresetMediaSwitch.IsToggled);
        Preferences.Set(AppManager.PresetMarketKey, PresetMarketSwitch.IsToggled);
        Preferences.Set(AppManager.PresetRuTldsKey, PresetRuTldsSwitch.IsToggled);
        AppManager.SyncPolicyFromPreferences();
        UpdateWindowsDynamicRouteCounter();
#endif
    }

    private void OnAddCustomRuleClicked(object? sender, EventArgs e)
    {
#if WINDOWS
        var raw = CustomRuleEntry.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        if (SplitTunnelPolicy.IsProtectedObxodkaEntry(raw))
        {
            _ = ShowSplitErrorAsync("Нельзя добавить домены или ресурсы obxodka в список исключений.");
            return;
        }

        var normalized = SplitTunnelPolicy.NormalizeRuleEntry(raw);
        if (SplitTunnelPolicy.IsProtectedObxodkaEntry(normalized))
        {
            _ = ShowSplitErrorAsync("Нельзя добавить домены или ресурсы obxodka в список исключений.");
            return;
        }

        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length < 3 ||
            !normalized.Contains('.') && !IPAddress.TryParse(normalized, out _))
        {
            _ = ShowSplitErrorAsync("Введите корректный домен (например, mybank.ru) или IPv4.");
            return;
        }

        ThemeAudioService.PlayClick();
        var current = AppManager.GetCustomRules();
        if (!current.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            current.Add(normalized);
            AppManager.SaveCustomRules(current);
        }

        CustomRuleEntry.Text = string.Empty;
        RenderCustomRulesChips();
        UpdateWindowsDynamicRouteCounter();
#endif
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
            catch
            {
                // ignored
            }
        }, token);
    }

    private void LoadWindowsSitesAndPresetsState()
    {
        _isUpdatingInternally = true;
        try
        {
            PresetGovSwitch.IsToggled = Preferences.Get(AppManager.PresetGovKey, true);
            PresetBanksSwitch.IsToggled = Preferences.Get(AppManager.PresetBanksKey, true);
            PresetMediaSwitch.IsToggled = Preferences.Get(AppManager.PresetMediaKey, true);
            PresetMarketSwitch.IsToggled = Preferences.Get(AppManager.PresetMarketKey, true);
            PresetRuTldsSwitch.IsToggled = Preferences.Get(AppManager.PresetRuTldsKey, true);
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
            ? WindowsSplitTunnelEngine.ActiveRouteCount
            : 0;
        var rulesCount = AppManager.GetCustomRules().Count;
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

        var routes = WindowsSplitTunnelEngine.ActiveBypassRoutes;
        if (routes.Count == 0)
        {
            var isConnected = _parent.VpnService.CurrentState == AppVpnState.Connected;
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
                StrokeShape = new RoundRectangle
                {
                    CornerRadius = 10
                }
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

        if (routes.Count <= maxVisibleChips)
        {
            return;
        }

        var moreChip = new Border
        {
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(9, 5),
            BackgroundColor = ActiveChipBg,
            Stroke = ActiveChipColor,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle
            {
                CornerRadius = 10
            }
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

    private void RenderCustomRulesChips()
    {
        CustomRulesFlexLayout.Children.Clear();
        var rules = AppManager.GetCustomRules();

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
                StrokeShape = new RoundRectangle
                {
                    CornerRadius = 14
                }
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
                    var list = AppManager.GetCustomRules();
                    _ = list.RemoveAll(r => r.Equals(targetRule, StringComparison.OrdinalIgnoreCase));
                    AppManager.SaveCustomRules(list);
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
}
