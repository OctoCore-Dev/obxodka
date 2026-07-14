namespace obxodka.Views;

public partial class SplitView : ContentView
{
    private MainPage _parent = null!;
    private IAppManager _appManager = null!;

    private List<AppInfoItem> _allApps = [];

    public static readonly BindableProperty IsSplitEditingAllowedProperty =
        BindableProperty.Create(nameof(IsSplitEditingAllowed), typeof(bool), typeof(SplitView), false);

    public bool IsSplitEditingAllowed
    {
        get => (bool)GetValue(IsSplitEditingAllowedProperty);
        set => SetValue(IsSplitEditingAllowedProperty, value);
    }

    public SplitView() => InitializeComponent();

    public void Initialize(MainPage parent, IAppManager appManager)
    {
        _parent = parent;
        _appManager = appManager;
        _parent.VpnService.OnStateChanged += (s) => MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_allApps.Count > 0)
                {
                    IsSplitEditingAllowed = s == AppVpnState.Disconnected;
                }
            });
    }

    public async Task LoadSplitAppsAsync()
    {
        if (_allApps.Count > 0)
        {
            var bypassed = _appManager.GetBypassedPackages().ToHashSet();
            foreach (var app in _allApps)
            {
                app.IsBypassed = bypassed.Contains(app.PackageName);
            }
            IsSplitEditingAllowed = _parent.VpnService.CurrentState == AppVpnState.Disconnected;
            return;
        }

        SplitLoadingOverlay.IsVisible = true;
        SplitAppsList.IsVisible = false;
        SplitAppsErrorLabel.Opacity = 0;

        try
        {
            var apps = await _appManager.GetInstalledAppsAsync();

            if (apps is not null)
            {
                _allApps = apps;
                RefreshSplitList();
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

    private void RefreshSplitList()
    {
        var q = SplitSearchEntry.Text?.Trim().ToLowerInvariant();
        IEnumerable<AppInfoItem> filtered = _allApps;
        if (!string.IsNullOrEmpty(q))
        {
            filtered = filtered.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || a.PackageName.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        SplitAppsList.ItemsSource = filtered.ToList();
    }

    private void OnSplitSearchTextChanged(object? sender, TextChangedEventArgs e) => RefreshSplitList();

    private async void OnSplitAppToggledAsync(object? sender, ToggledEventArgs e)
    {
        if (sender is not Microsoft.Maui.Controls.Switch { BindingContext: AppInfoItem app })
        {
            return;
        }

        if (!IsSplitEditingAllowed)
        {
            app.IsBypassed = !e.Value;
            return;
        }

        try
        {
            app.IsBypassed = e.Value;
            var bypassed = _allApps.Where(a => a.IsBypassed).Select(a => a.PackageName).ToList();
            _appManager.SaveBypassedPackages(bypassed);
        }
        catch
        {
            app.IsBypassed = !e.Value;
            await _parent.DisplayAlertAsync("Ошибка", "Не удалось сохранить настройку.", "OK");
        }
    }

    private async Task ShowSplitErrorAsync(string msg)
    {
        SplitAppsErrorLabel.Text = msg;
        _ = SplitAppsErrorLabel.FadeToAsync(1, 200);
        await this.ShakeErrorAsync();
    }
}
