namespace obxodka.Pages;
internal partial class SplitTunnelingPage : ContentPage
{
    private List<AppInfoItem> _allApps = new();
    private ObservableCollection<AppInfoItem> _displayedApps = new();
    private readonly IVpnService _vpnService;
    public bool IsEditingAllowed { get; set; }
    public SplitTunnelingPage(IVpnService vpnService)
    {
        InitializeComponent();
        _vpnService = vpnService;
        AppsList.ItemsSource = _displayedApps;
        BindingContext = this;
#if WINDOWS
        WindowsAddBtn.IsVisible = true;
#else
        WindowsAddBtn.IsVisible = false;
#endif
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        bool isVpnRunning = _vpnService.IsRunning;
        IsEditingAllowed = !isVpnRunning;
        OnPropertyChanged(nameof(IsEditingAllowed));
        VpnWarningBanner.IsVisible = isVpnRunning;
        _ = PlayCascadeAnimationAsync();
        if (_allApps.Count == 0)
        {
            try
            {
                Loader.IsRunning = true;
                Loader.IsVisible = true;
                var loadedApps = await Task.Run(() => AppScanner.GetInstalledApps());
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    _allApps = loadedApps;
                    FilterList(AppSearchBar.Text ?? "");
                    Loader.IsRunning = false;
                    Loader.IsVisible = false;
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LOAD ERROR] Apps: {ex.Message}");
                Loader.IsVisible = false;
            }
        }
    }
    private async Task PlayCascadeAnimationAsync()
    {
        HeaderSection.Opacity = 0; HeaderSection.Scale = 0.8;
        SearchSection.Opacity = 0; SearchSection.Scale = 0.8;
        AppsList.Opacity = 0; AppsList.Scale = 0.8;
        _ = HeaderSection.FadeToAsync(1, 400);
        _ = HeaderSection.ScaleToAsync(1, 400, Easing.SpringOut);
        await Task.Delay(100);
        _ = SearchSection.FadeToAsync(1, 400);
        _ = SearchSection.ScaleToAsync(1, 400, Easing.SpringOut);
        await Task.Delay(100);
        _ = AppsList.FadeToAsync(1, 400);
        _ = AppsList.ScaleToAsync(1, 400, Easing.SpringOut);
    }
    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        FilterList(e.NewTextValue);
    }
    private void FilterList(string query)
    {
        _displayedApps.Clear();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allApps
            : _allApps.Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                  a.PackageName.Contains(query, StringComparison.OrdinalIgnoreCase));
        foreach (var app in filtered)
        {
            _displayedApps.Add(app);
        }
    }
    private void OnAppToggled(object? sender, ToggledEventArgs e)
    {
        if (IsEditingAllowed)
        {
            AppScanner.SaveExcludedApps(_allApps);
        }
    }
    private async void OnWindowsAddClicked(object? sender, EventArgs e)
    {
#if WINDOWS
        if (!IsEditingAllowed) return;
        var result = await AppScanner.PickWindowsAppAsync();
        if (result != null)
        {
            if (!_allApps.Any(a => a.PackageName == result.PackageName))
            {
                _allApps.Insert(0, result);
                FilterList(AppSearchBar.Text);
                AppScanner.SaveExcludedApps(_allApps);
            }
        }
#else
        await Task.CompletedTask;
#endif
    }
}