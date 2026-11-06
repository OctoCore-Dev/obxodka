namespace obxodka.Pages;

//экран списка авторизованных устройств пользователя
public sealed partial class DevicesView : ContentView
{
    private static readonly TimeSpan s_cacheTtl = TimeSpan.FromSeconds(30);

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

        DevicesScrollView.Scrolled += (s, e) =>
            CardStackScrollHelper.ApplyToChildren(
                DevicesScrollView,
                DevicesGrid.Children,
                zone: 65.0,
                minScale: 0.92,
                power: 2.5);
    }

    //расчет ширины для перестроения интерфейса
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

    //подгонка ширины карточек устройств
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

    //инициализация зависимостей экрана устройств
    public void Initialize(MainPage parent, ApiService apiService)
    {
        _parent = parent;
        _apiService = apiService;
    }

    //каскадная анимация появления списка устройств
    public async Task PlayEntranceAnimationAsync()
    {
        Opacity = 1;
        TranslationY = 0;
        await this.PlayCardsEntranceAsync(35, 240);
    }

    //загрузка списка активных сессий с сервера
    public async Task LoadDevicesAsync()
    {
        if (ConnectedDevices.Count > 0 && DateTime.UtcNow - _lastFetchTime < s_cacheTtl)
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
                await ShowDevicesErrorAsync(ApiErrorHandler.ParseGeneralError(error, AccountMessages.LoadDevicesFailed));
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[DEVICES LOAD ERROR] {ex.Message}", ex);
            if (ConnectedDevices.Count == 0)
            {
                await ShowDevicesErrorAsync(ApiErrorHandler.ParseGeneralError(ex.Message, AccountMessages.ConnectionProblem));
            }
        }
        finally
        {
            RebuildDevicesGrid();
            DevicesLoadingOverlay.IsVisible = false;
            DevicesGrid.IsVisible = true;

            if (ConnectedDevices.Count > 0)
            {
                _ = PlayEntranceAnimationAsync();
            }
        }
    }

    //сброс кэша времени обновления устройств
    public void InvalidateCache() => _lastFetchTime = DateTime.MinValue;

    //обновление темы карточек устройств
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

    //отступ сверху под системные элементы управления
    public void SetHeaderTopInset(double top)
    {
        if (DeviceInfo.Platform == DevicePlatform.Android && DeviceInfo.Idiom == DeviceIdiom.Phone && RootLayout != null)
        {
            var topAir = Math.Max(top + 16, 50);
            RootLayout.Padding = new Thickness(16, topAir, 16, 0);
        }
        else if (DeviceInfo.Idiom == DeviceIdiom.Phone && RootLayout != null)
        {
            RootLayout.Padding = new Thickness(16, Math.Max(top + 4, 12), 16, 0);
        }
    }

    //перерисовка плитки при смене темы оформления
    public void OnThemeChanged() =>
        MainThread.BeginInvokeOnMainThread(RebuildDevicesGrid);

    //построение плитки устройств с текущим во главе
    private void RebuildDevicesGrid()
    {
        foreach (var child in DevicesGrid.Children)
        {
            if (child is DeviceCardView oldCard)
            {
                oldCard.RemoveClicked -= OnRemoveDeviceClickedAsync;
            }
        }

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

    //отзыв сессии выбранного устройства
    private async void OnRemoveDeviceClickedAsync(object? sender, EventArgs e)
    {
        var hwidToRemove = ((sender as Element)?.BindingContext as DeviceItem)?.Hwid;
        if (string.IsNullOrEmpty(hwidToRemove))
        {
            return;
        }

        if (hwidToRemove == DeviceHelper.Hwid)
        {
            await ShowDevicesErrorAsync(AccountMessages.CannotDeleteCurrentDevice);
            return;
        }

        var confirm = await _parent.DisplayAlertAsync(AccountMessages.DeleteDeviceTitle, AccountMessages.DeleteDeviceConfirm, AccountMessages.ButtonConfirmYes, AccountMessages.ButtonCancel);
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
                        await ShowDevicesErrorAsync(ApiErrorHandler.ParseGeneralError(error, AccountMessages.RemoveDeviceFailed));
                    });
                }
                else
                {
                    _lastFetchTime = DateTime.MinValue;
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"[REMOVE DEVICE ERROR] {ex.Message}", ex);
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    if (item is not null)
                    {
                        ConnectedDevices.Add(item);
                        RebuildDevicesGrid();
                    }
                    await ShowDevicesErrorAsync(ApiErrorHandler.ParseGeneralError(ex.Message, AccountMessages.ConnectionProblem));
                });
            }
        });
    }

    //показ плашки ошибки с анимацией
    private async Task ShowDevicesErrorAsync(string msg)
    {
        DevicesErrorLabel.Text = msg;
        await UIAnimations.ShowErrorLabelAsync(DevicesErrorLabel);
        await this.ShakeErrorAsync();
    }
}
