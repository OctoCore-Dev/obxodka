using Microsoft.Maui.Controls.Shapes;
using Switch = Microsoft.Maui.Controls.Switch;

namespace obxodka.Views;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "ContentView lifecycle is managed by MAUI visual tree")]
public sealed partial class SplitView : ContentView
{
    private static Color ActiveChipColor => (Application.Current?.Resources.TryGetValue("Primary", out var val) == true && val is Color c)
        ? c
        : Color.FromArgb("#7C3AED");
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
        HeaderContainer.SizeChanged += OnHeaderContainerSizeChanged;
#if ANDROID
        SplitAppsList.HandlerChanged += OnSplitAppsListHandlerChanged;
        Loaded += (_, _) => AttachScrollListener();
#endif
    }

    public void Initialize(MainPage parent, IAppManager appManager)
    {
        _parent = parent;
        _appManager = appManager;
        _parent.VpnService.OnStateChanged += OnVpnStateChanged;
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        HeaderContainer.SizeChanged -= OnHeaderContainerSizeChanged;
        _parent?.VpnService.OnStateChanged -= OnVpnStateChanged;
#if ANDROID
        SplitAppsList.HandlerChanged -= OnSplitAppsListHandlerChanged;
        DetachScrollListener();
#endif
    }

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
                TextColor = ActiveChipColor,
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
                    IconColor = ErrorColor,
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
        if (_showOnlyBypassed)
        {
            ChipFilterAll.BackgroundColor = InactiveChipBg;
            ChipFilterAll.Stroke = InactiveChipStroke;
            FilterAllLabel.TextColor = InactiveChipText;

            ChipFilterBypassed.BackgroundColor = ActiveChipBg;
            ChipFilterBypassed.Stroke = ActiveChipColor;
            FilterBypassedLabel.TextColor = ActiveChipColor;
        }
        else
        {
            ChipFilterAll.BackgroundColor = ActiveChipBg;
            ChipFilterAll.Stroke = ActiveChipColor;
            FilterAllLabel.TextColor = ActiveChipColor;

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

        var bgBase = (Application.Current?.Resources.TryGetValue("BgBase", out var bgb) == true && bgb is Color bgBaseColor)
            ? bgBaseColor
            : Color.FromArgb("#0E0E14");

        HeaderContainer.BackgroundColor = bgBase.WithAlpha(0.92f);
        ChipFilterBypassed.BackgroundColor = bgSurface;
        ChipResetAll.BackgroundColor = bgSurface;

        if (SplitAppsList.ItemsSource != null && _allApps.Count > 0)
        {
            var currentItems = SplitAppsList.ItemsSource;
            SplitAppsList.ItemsSource = null;
            SplitAppsList.ItemsSource = currentItems;
        }
    }

    public void SetHeaderTopInset(double top)
    {
        if (DeviceInfo.Idiom == DeviceIdiom.Phone)
        {
            var safeTop = Math.Max(top, 10);
            HeaderContainer.Padding = new Thickness(16, safeTop, 16, 10);
            UpdateHeaderSpacerHeight();
        }
    }

    private void OnHeaderContainerSizeChanged(object? sender, EventArgs e) =>
        UpdateHeaderSpacerHeight();

    private void UpdateHeaderSpacerHeight()
    {
        if (HeaderContainer.Height > 0)
        {
            var targetHeight = HeaderContainer.Height + 8;
            if (Math.Abs(SplitListHeaderSpacer.HeightRequest - targetHeight) > 1.0)
            {
                SplitListHeaderSpacer.HeightRequest = targetHeight;
            }
        }
    }

    public void OnThemeChanged() =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateCardOpacity();
            UpdateChipsAndCounters();
        });

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

#if ANDROID
    private CardStackScrollHelper.CardStackRecyclerViewScrollListener? _scrollListener;
    private AndroidX.RecyclerView.Widget.RecyclerView? _attachedRecyclerView;

    private void OnSplitAppsListHandlerChanged(object? sender, EventArgs e) =>
        AttachScrollListener();

    private void AttachScrollListener()
    {
        if (SplitAppsList.Handler?.PlatformView is Android.Views.ViewGroup viewGroup)
        {
            var recyclerView = viewGroup as AndroidX.RecyclerView.Widget.RecyclerView
                ?? FindRecyclerView(viewGroup);

            if (recyclerView != null && recyclerView != _attachedRecyclerView)
            {
                DetachScrollListener();
                _attachedRecyclerView = recyclerView;
                _scrollListener = new CardStackScrollHelper.CardStackRecyclerViewScrollListener();
                _attachedRecyclerView.AddOnScrollListener(_scrollListener);
            }
        }
    }

    private void DetachScrollListener()
    {
        if (_attachedRecyclerView != null && _scrollListener != null)
        {
            _attachedRecyclerView.RemoveOnScrollListener(_scrollListener);
            _scrollListener.Dispose();
            _scrollListener = null;
            _attachedRecyclerView = null;
        }
    }

    private static AndroidX.RecyclerView.Widget.RecyclerView? FindRecyclerView(Android.Views.ViewGroup parent)
    {
        for (var i = 0; i < parent.ChildCount; i++)
        {
            var child = parent.GetChildAt(i);
            if (child is AndroidX.RecyclerView.Widget.RecyclerView rv)
            {
                return rv;
            }
            if (child is Android.Views.ViewGroup vg)
            {
                var nested = FindRecyclerView(vg);
                if (nested != null)
                {
                    return nested;
                }
            }
        }
        return null;
    }
#endif
}
