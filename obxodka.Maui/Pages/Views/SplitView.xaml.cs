using Microsoft.Maui.Controls.Shapes;
using Switch = Microsoft.Maui.Controls.Switch;

namespace obxodka.Views;

public sealed partial class SplitView : ContentView
{
    private static readonly Color t_activeChipBgLight = Color.FromArgb("#207C3AED");
    private static readonly Color t_activeChipBgDark = Color.FromArgb("#307C3AED");
    private static readonly Color t_activeChipStroke = Color.FromArgb("#7C3AED");
    private static readonly Color t_activeChipText = Color.FromArgb("#7C3AED");

    private static readonly Color t_inactiveChipBgLight = Color.FromArgb("#15000000");
    private static readonly Color t_inactiveChipBgDark = Color.FromArgb("#15FFFFFF");
    private static readonly Color t_inactiveChipStrokeLight = Color.FromArgb("#30000000");
    private static readonly Color t_inactiveChipStrokeDark = Color.FromArgb("#30FFFFFF");
    private static readonly Color t_inactiveChipTextLight = Color.FromArgb("#6B7280");
    private static readonly Color t_inactiveChipTextDark = Color.FromArgb("#9CA3AF");

    private static readonly Color t_errorColor = Color.FromArgb("#EF4444");

    private MainPage _parent = null!;
    private IAppManager _appManager = null!;

    private List<AppInfoItem> _allApps = [];
    private bool _showOnlyBypassed;
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
    }

    public void Initialize(MainPage parent, IAppManager appManager)
    {
        _parent = parent;
        _appManager = appManager;
        _parent.VpnService.OnStateChanged += OnVpnStateChanged;
    }

    private void OnUnloaded(object? sender, EventArgs e) =>
        _parent?.VpnService.OnStateChanged -= OnVpnStateChanged;

    private void OnVpnStateChanged(AppVpnState s) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_allApps.Count > 0)
            {
                IsSplitEditingAllowed = s == AppVpnState.Disconnected;
            }
        });

    public async Task LoadSplitAppsAsync()
    {
        if (_allApps.Count > 0)
        {
            var bypassed = _appManager.GetBypassedPackages().ToHashSet();
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
            IsSplitEditingAllowed = _parent.VpnService.CurrentState == AppVpnState.Disconnected;
            return;
        }

        SplitLoadingOverlay.IsVisible = true;
        SplitAppsList.IsVisible = false;
        _ = UIAnimations.HideErrorLabelAsync(SplitAppsErrorLabel);

        try
        {
            var apps = await _appManager.GetInstalledAppsAsync();
            if (apps is not null)
            {
                _allApps = apps;
                ApplyFilterAndRenderList();
                UpdateChipsAndCounters();
                IsSplitEditingAllowed = _parent.VpnService.CurrentState == AppVpnState.Disconnected;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SPLIT ERROR] {ex.Message}");
            await ShowSplitErrorAsync("Ошибка загрузки приложений.");
            IsSplitEditingAllowed = false;
        }
        finally
        {
            SplitLoadingOverlay.IsVisible = false;
            SplitAppsList.IsVisible = true;
        }
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
                BackgroundColor = t_activeChipBgDark,
                Stroke = t_activeChipStroke,
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

            var icon = new Image
            {
                Source = app.IconPath is { Length: > 0 } ? ImageSource.FromFile(app.IconPath) : null,
                HeightRequest = 18,
                WidthRequest = 18,
                VerticalOptions = LayoutOptions.Center
            };

            var nameLabel = new Label
            {
                Text = app.Name,
                FontSize = 12,
                FontFamily = "RobotoMedium",
                TextColor = t_activeChipText,
                VerticalOptions = LayoutOptions.Center
            };

            var removeBtn = new Border
            {
                Padding = new Thickness(2),
                BackgroundColor = Colors.Transparent,
                StrokeThickness = 0,
                VerticalOptions = LayoutOptions.Center,
                Content = new MauiIcon
                {
                    Icon = FluentIcons.Dismiss16,
                    IconColor = t_errorColor,
                    IconSize = 14
                }
            };

            var targetApp = app;
            removeBtn.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(async () => await RemoveFromBypassAsync(targetApp))
            });

            Grid.SetColumn(icon, 0);
            Grid.SetColumn(nameLabel, 1);
            Grid.SetColumn(removeBtn, 2);

            grid.Children.Add(icon);
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
        _showOnlyBypassed = false;
        UpdateFilterChipStyles();
        ApplyFilterAndRenderList();
    }

    private void OnFilterBypassedClicked(object? sender, EventArgs e)
    {
        _showOnlyBypassed = true;
        UpdateFilterChipStyles();
        ApplyFilterAndRenderList();
    }

    private void UpdateFilterChipStyles()
    {
        var isDark = Application.Current?.RequestedTheme != AppTheme.Light;

        if (_showOnlyBypassed)
        {
            ChipFilterAll.BackgroundColor = isDark ? t_inactiveChipBgDark : t_inactiveChipBgLight;
            ChipFilterAll.Stroke = isDark ? t_inactiveChipStrokeDark : t_inactiveChipStrokeLight;
            FilterAllLabel.TextColor = isDark ? t_inactiveChipTextDark : t_inactiveChipTextLight;

            ChipFilterBypassed.BackgroundColor = isDark ? t_activeChipBgDark : t_activeChipBgLight;
            ChipFilterBypassed.Stroke = t_activeChipStroke;
            FilterBypassedLabel.TextColor = t_activeChipText;
        }
        else
        {
            ChipFilterAll.BackgroundColor = isDark ? t_activeChipBgDark : t_activeChipBgLight;
            ChipFilterAll.Stroke = t_activeChipStroke;
            FilterAllLabel.TextColor = t_activeChipText;

            ChipFilterBypassed.BackgroundColor = isDark ? t_inactiveChipBgDark : t_inactiveChipBgLight;
            ChipFilterBypassed.Stroke = isDark ? t_inactiveChipStrokeDark : t_inactiveChipStrokeLight;
            FilterBypassedLabel.TextColor = isDark ? t_inactiveChipTextDark : t_inactiveChipTextLight;
        }
    }

    private async void OnResetAllBypassedClickedAsync(object? sender, EventArgs e)
    {
        if (!IsSplitEditingAllowed)
        {
            await _parent.DisplayAlertAsync("Внимание", "Отключите VPN, чтобы изменить список приложений.", "OK");
            return;
        }

        var confirmed = await _parent.DisplayAlertAsync("Сброс", "Сбросить все правила обхода VPN?", "Сбросить", "Отмена");
        if (!confirmed)
        {
            return;
        }

        _isUpdatingInternally = true;
        foreach (var app in _allApps)
        {
            app.IsBypassed = false;
        }
        _isUpdatingInternally = false;

        _appManager.SaveBypassedPackages([]);
        UpdateChipsAndCounters();
    }

    private void OnAppRowTapped(object? sender, EventArgs e)
    {
        if (sender is VisualElement { BindingContext: AppInfoItem app })
        {
            if (!IsSplitEditingAllowed)
            {
                return;
            }

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

    private async Task ShowSplitErrorAsync(string msg)
    {
        SplitAppsErrorLabel.Text = msg;
        await UIAnimations.ShowErrorLabelAsync(SplitAppsErrorLabel);
        await this.ShakeErrorAsync();
    }
}
