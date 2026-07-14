namespace obxodka.Pages;

internal sealed partial class MainPage
{
    private async Task LoadSplitAppsAsync()
    {
        var isVpnRunning = _vpnService.IsRunning;
        IsSplitEditingAllowed = !isVpnRunning;
        OnPropertyChanged(nameof(IsSplitEditingAllowed));
        SplitVpnWarning.IsVisible = isVpnRunning;

        if (_allApps.Count > 0)
        {
            return;
        }

        try
        {
            SplitLoader.IsRunning = true;
            SplitLoader.IsVisible = true;
            var loaded = await Task.Run(AppScanner.GetInstalledApps);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _allApps = loaded;
                FilterSplitList(SplitSearchBar.Text ?? "");
                SplitLoader.IsRunning = false;
                SplitLoader.IsVisible = false;
            });
        }
        catch { SplitLoader.IsVisible = false; }
    }

    private void OnSplitSearchTextChanged(object? sender, TextChangedEventArgs e)
        => FilterSplitList(e.NewTextValue);

    private void FilterSplitList(string query)
    {
        _displayedApps.Clear();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allApps
            : _allApps.Where(a =>
                a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                a.PackageName.Contains(query, StringComparison.OrdinalIgnoreCase));

        foreach (var app in filtered)
        {
            _displayedApps.Add(app);
        }
    }

    private void OnSplitAppToggled(object? sender, ToggledEventArgs e)
    {
        if (IsSplitEditingAllowed)
        {
            AppScanner.SaveExcludedApps(_allApps);
        }
    }
}
