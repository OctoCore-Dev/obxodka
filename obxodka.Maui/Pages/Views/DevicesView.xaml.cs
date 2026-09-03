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

        if (DeviceInfo.Idiom == DeviceIdiom.Phone)
        {
            _ = ContentGrid.Children.Remove(DevicesGrid);
            var scroll = new ScrollView
            {
                Orientation = ScrollOrientation.Vertical,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
                VerticalScrollBarVisibility = ScrollBarVisibility.Never,
                Content = DevicesGrid
            };
            ContentGrid.Children.Add(scroll);
        }
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

        var cards = DevicesGrid.Children.OfType<VisualElement>().ToArray();
        if (cards.Length > 0)
        {
            await UIAnimations.PlayEntranceCascadeAsync(80, 450, cards);
        }
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

    private void RebuildDevicesGrid()
    {
        DevicesGrid.Children.Clear();
        DevicesGrid.RowDefinitions.Clear();

        if (ConnectedDevices.Count == 0)
        {
            return;
        }

        var columnsCount = DeviceInfo.Idiom == DeviceIdiom.Desktop ? 2 : 1;
        var col = 0;
        var row = 0;

        foreach (var device in ConnectedDevices)
        {
            if (col == 0)
            {
                DevicesGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            var card = new DeviceCardView { BindingContext = device };
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
