namespace obxodka.Pages.Account;

//экран списка авторизованных устройств пользователя
public sealed partial class DevicesView : IAdaptivePage
{
    private static readonly TimeSpan s_cacheTtl = TimeSpan.FromSeconds(30);
    private ApiService _apiService = null!;
    private int _columnsCount = 1;
    private DateTime _lastFetchTime = DateTime.MinValue;

    private MainPage _parent = null!;

    public DevicesView() => InitializeComponent();

    public ObservableCollection<DeviceItem> ConnectedDevices { get; set; } = [];

    //перерисовка плитки при смене темы оформления
    public void OnThemeChanged() =>
        MainThread.BeginInvokeOnMainThread(RebuildDevicesGrid);

    //сброс кэша времени обновления устройств
    public void InvalidateCache() => _lastFetchTime = DateTime.MinValue;

    //единая адаптация экрана устройств по ssot-снимку геометрии окна без ручного widthrequest
    public void ApplyViewport(AdaptiveViewport viewport)
    {
        if (RootLayout is null || DevicesGrid is null)
        {
            return;
        }

        var isCompact = viewport.IsCompact;
        var baseSidePad = isCompact ? 16.0 : viewport.IsExpanded ? 28.0 : 20.0;
        var sidePad = viewport.ComputeHorizontalPadding(baseSidePad, 960.0);
        var topPad = DeviceInfo.Platform == DevicePlatform.Android || DeviceInfo.Platform == DevicePlatform.iOS
            ? Math.Max(viewport.SafeTop + 14.0, isCompact ? 42.0 : 24.0)
            : isCompact ? 12.0 : 16.0;
        var bottomPad = !viewport.UseSidebar
            ? Math.Max(116.0, viewport.SafeBottom + 98.0)
            : Math.Max(32.0, viewport.SafeBottom + 20.0);

        RootLayout.Padding = new Thickness(sidePad, topPad, sidePad, 0);
        DevicesGrid.HorizontalOptions = LayoutOptions.Fill;
        DevicesGrid.Padding = new Thickness(0, 0, 0, bottomPad);

        var newCols = viewport.UseMultiColumnContent ? 2 : 1;
        if (_columnsCount == newCols)
        {
            return;
        }

        _columnsCount = newCols;
        DevicesGrid.ColumnDefinitions.Clear();
        DevicesGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        if (newCols == 2)
        {
            DevicesGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            DevicesGrid.ColumnSpacing = 16;
        }
        else
        {
            DevicesGrid.ColumnSpacing = 0;
        }

        RebuildDevicesGrid();
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
        _ = UiAnimations.HideErrorLabelAsync(DevicesErrorLabel);

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

    //обновление темы карточек устройств
    public void UpdateCardOpacity()
    {
        var bgSurface = Application.Current?.Resources.TryGetValue("BgSurface", out var bg) == true && bg is Color bgColor
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

        var bgSurface = Application.Current?.Resources.TryGetValue("BgSurface", out var bg) == true && bg is Color bgColor
            ? bgColor
            : Color.FromArgb("#161622");

        var currentHwid = DeviceHelper.Hwid;
        var orderedDevices = ConnectedDevices
            .OrderByDescending(d => !string.IsNullOrEmpty(d.Hwid) && string.Equals(d.Hwid, currentHwid, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(d => d.LastActive)
            .ToList();

        var columnsCount = _columnsCount;
        var col = 0;
        var row = 0;

        foreach (var device in orderedDevices)
        {
            if (col == 0)
            {
                DevicesGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            var isCurrent = !string.IsNullOrEmpty(device.Hwid) &&
                            string.Equals(device.Hwid, currentHwid, StringComparison.OrdinalIgnoreCase);

            var card = new DeviceCardView
            {
                BindingContext = device
            };
            card.SetIsCurrentDevice(isCurrent);
            card.UpdateCardOpacity(bgSurface);
            card.RemoveClicked += OnRemoveDeviceClickedAsync;

            Grid.SetRow(card, row);
            Grid.SetColumn(card, col);
            DevicesGrid.Children.Add(card);

            col++;
            if (col < columnsCount)
            {
                continue;
            }

            col = 0;
            row++;
        }
    }

    //отзыв сессии выбранного устройства
    private async void OnRemoveDeviceClickedAsync(object? sender, EventArgs e)
    {
        try
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

            var confirm = await _parent.DisplayAlertAsync(AccountMessages.DeleteDeviceTitle, AccountMessages.DeleteDeviceConfirm,
                AccountMessages.ButtonConfirmYes, AccountMessages.ButtonCancel);
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
        catch (Exception ex)
        {
            AppLogger.LogError($"[REMOVE DEVICE ERROR] {ex.Message}", ex);
            await ShowDevicesErrorAsync(ApiErrorHandler.ParseGeneralError(ex.Message, AccountMessages.ConnectionProblem));
        }
    }

    //показ плашки ошибки с анимацией
    private async Task ShowDevicesErrorAsync(string msg)
    {
        DevicesErrorLabel.Text = msg;
        await UiAnimations.ShowErrorLabelAsync(DevicesErrorLabel);
        await this.ShakeErrorAsync();
    }
}
