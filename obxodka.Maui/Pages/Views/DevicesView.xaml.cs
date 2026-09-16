namespace obxodka.Views;

public sealed partial class DevicesView : ContentView
{
    private static readonly TimeSpan t_cacheTtl = TimeSpan.FromSeconds(30);

    private MainPage _parent = null!;
    private ApiService _apiService = null!;
    private DateTime _lastFetchTime = DateTime.MinValue;

    public ObservableCollection<DeviceItem> ConnectedDevices { get; set; } = [];

    public DevicesView()
    {
        InitializeComponent();

        DevicesScrollView.SizeChanged += (s, e) =>
        {
            if (DevicesScrollView.Width > 0)
            {
                ApplyCardWidth(DevicesScrollView.Width);
            }
        };
    }

    public void ForceLayoutWidth()
    {
        if (DevicesScrollView.Width > 0)
        {
            ApplyCardWidth(DevicesScrollView.Width);
        }
        else if (Width > 0 && RootLayout is not null)
        {
            var avail = Width - RootLayout.Padding.HorizontalThickness;
            if (avail > 0)
            {
                ApplyCardWidth(avail);
            }
        }
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width > 0 && RootLayout is not null)
        {
            var availableWidth = width - RootLayout.Padding.HorizontalThickness;
            if (availableWidth > 0)
            {
                ApplyCardWidth(availableWidth);
            }
        }
    }

    private void ApplyCardWidth(double targetWidth)
    {
        if (targetWidth <= 0 || DevicesGrid is null)
        {
            return;
        }

        var safeWidth = Math.Min(targetWidth - 6, 950);
        if (safeWidth <= 0)
        {
            return;
        }

        DevicesGrid.WidthRequest = safeWidth;
        DevicesGrid.MaximumWidthRequest = safeWidth;
    }

    public void Initialize(MainPage parent, ApiService apiService)
    {
        _parent = parent;
        _apiService = apiService;
    }

    public async Task PlayEntranceAnimationAsync()
    {
        Opacity = 1;
        TranslationY = 0;
        await this.PlayCardsEntranceAsync(35, 240);
    }

    public async Task LoadDevicesAsync()
    {
        if (ConnectedDevices.Count > 0 && DateTime.UtcNow - _lastFetchTime < t_cacheTtl)
        {
            return;
        }

        DevicesLoadingOverlay.IsVisible = true;
        DevicesGrid.IsVisible = false;
        _ = UIAnimations.HideErrorLabelAsync(DevicesErrorLabel);

        try
        {
            var (success, devices, error) = await _apiService.GetDevicesAsync();
            if (success && devices is not null)
            {
                ConnectedDevices.Clear();
                foreach (var d in devices)
                {
                    ConnectedDevices.Add(d);
                }
                _lastFetchTime = DateTime.UtcNow;
            }
            else if (!string.IsNullOrEmpty(error) && ConnectedDevices.Count == 0)
            {
                await ShowDevicesErrorAsync(ApiErrorHandler.ParseGeneralError(error, "Не удалось загрузить список устройств."));
            }
        }
        catch
        {
            if (ConnectedDevices.Count == 0)
            {
                await ShowDevicesErrorAsync("Проблема с соединением.");
            }
        }
        finally
        {
            RebuildDevicesGrid();
            DevicesLoadingOverlay.IsVisible = false;
            DevicesGrid.IsVisible = true;

            if (ConnectedDevices.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100);
                    await MainThread.InvokeOnMainThreadAsync(PlayEntranceAnimationAsync);
                });
            }
        }
    }

    public void InvalidateCache() => _lastFetchTime = DateTime.MinValue;

    public void UpdateCardOpacity()
    {
        var bgSurface = (Application.Current?.Resources.TryGetValue("BgSurface", out var bg) == true && bg is Color bgColor)
            ? bgColor
            : Color.FromArgb("#161622");

        foreach (var child in DevicesGrid.Children)
        {
            if (child is DeviceCardView card)
            {
                card.UpdateCardOpacity(bgSurface);
            }
        }
    }

    public void SetHeaderTopInset(double top)
    {
        if (DeviceInfo.Idiom == DeviceIdiom.Phone && RootLayout != null)
        {
            RootLayout.Padding = new Thickness(16, Math.Max(top + 4, 12), 16, 0);
        }
    }

    public void OnThemeChanged() =>
        MainThread.BeginInvokeOnMainThread(RebuildDevicesGrid);

    private void RebuildDevicesGrid()
    {
        DevicesGrid.Children.Clear();
        DevicesGrid.RowDefinitions.Clear();

        if (ConnectedDevices.Count == 0)
        {
            return;
        }

        var bgSurface = (Application.Current?.Resources.TryGetValue("BgSurface", out var bg) == true && bg is Color bgColor)
            ? bgColor
            : Color.FromArgb("#161622");

        var currentHwid = DeviceHelper.Hwid;
        var orderedDevices = ConnectedDevices
            .OrderByDescending(d => !string.IsNullOrEmpty(d.Hwid) && string.Equals(d.Hwid, currentHwid, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(d => d.LastActive)
            .ToList();

        var columnsCount = DeviceInfo.Idiom == DeviceIdiom.Desktop ? 2 : 1;
        var col = 0;
        var row = 0;

        foreach (var device in orderedDevices)
        {
            if (col == 0)
            {
                DevicesGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            var isCurrent = !string.IsNullOrEmpty(device.Hwid) && string.Equals(device.Hwid, currentHwid, StringComparison.OrdinalIgnoreCase);
            device.IsCurrentDevice = isCurrent;

            var card = new DeviceCardView { BindingContext = device };
            card.SetIsCurrentDevice(isCurrent);
            card.UpdateCardOpacity(bgSurface);
            card.RemoveClicked += OnRemoveDeviceClickedAsync;

            Grid.SetRow(card, row);
            Grid.SetColumn(card, col);
            DevicesGrid.Children.Add(card);

            col++;
            if (col >= columnsCount)
            {
                col = 0;
                row++;
            }
        }
    }

    private async void OnRemoveDeviceClickedAsync(object? sender, EventArgs e)
    {
        var hwidToRemove = ((sender as Element)?.BindingContext as DeviceItem)?.Hwid;
        if (string.IsNullOrEmpty(hwidToRemove))
        {
            return;
        }

        if (hwidToRemove == DeviceHelper.Hwid)
        {
            await ShowDevicesErrorAsync("Нельзя удалить текущее устройство.");
            return;
        }

        var confirm = await _parent.DisplayAlertAsync("Удаление", "Удалить это устройство?", "Да", "Отмена");
        if (!confirm)
        {
            return;
        }

        var item = ConnectedDevices.FirstOrDefault(d => d.Hwid == hwidToRemove);
        if (item is not null)
        {
            _ = ConnectedDevices.Remove(item);
            RebuildDevicesGrid();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var (success, error) = await _apiService.RemoveDeviceAsync(hwidToRemove);
                if (!success)
                {
                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        if (item is not null)
                        {
                            ConnectedDevices.Add(item);
                            RebuildDevicesGrid();
                        }
                        await ShowDevicesErrorAsync(ApiErrorHandler.ParseGeneralError(error, "Не удалось удалить устройство."));
                    });
                }
                else
                {
                    _lastFetchTime = DateTime.MinValue;
                }
            }
            catch
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    if (item is not null)
                    {
                        ConnectedDevices.Add(item);
                        RebuildDevicesGrid();
                    }
                    await ShowDevicesErrorAsync("Проблема с соединением.");
                });
            }
        });
    }

    private async Task ShowDevicesErrorAsync(string msg)
    {
        DevicesErrorLabel.Text = msg;
        await UIAnimations.ShowErrorLabelAsync(DevicesErrorLabel);
        await this.ShakeErrorAsync();
    }
}
