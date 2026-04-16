namespace obxodka.Pages;
internal sealed class DeviceItem
{
    [JsonPropertyName("hwid")] public string Hwid { get; set; } = string.Empty;
    [JsonPropertyName("deviceName")] public string DeviceName { get; set; } = string.Empty;
    [JsonPropertyName("lastLogin")] public DateTime LastLogin { get; set; }
    [JsonIgnore] public string LastLoginText => $"Активен: {LastLogin.ToLocalTime():dd.MM.yyyy HH:mm}";
}
internal partial class DevicesPage : ContentPage
{
    private readonly ApiService _apiService;
    public ObservableCollection<DeviceItem> ConnectedDevices { get; } = new();
    public DevicesPage(ApiService apiService)
    {
        InitializeComponent();
        _apiService = apiService;
        DevicesList.ItemsSource = ConnectedDevices;
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        this.Content.Opacity = 0;
        this.Content.TranslationY = 20;
        _ = this.Content.FadeToAsync(1, 500);
        _ = this.Content.TranslateToAsync(0, 0, 500, Easing.SpringOut);
        await LoadDevicesAsync().ConfigureAwait(true);
    }
    private async Task LoadDevicesAsync()
    {
        LoadingOverlay.IsVisible = true;
        ConnectedDevices.Clear();
        var (success, devices, error) = await _apiService.GetDevicesAsync().ConfigureAwait(true);
        if (success && devices != null)
        {
            foreach (var d in devices) ConnectedDevices.Add(d);
        }
        else if (!string.IsNullOrEmpty(error))
        {
            await DisplayAlertAsync("Ошибка", "Не удалось загрузить список устройств.", "OK").ConfigureAwait(true);
        }
        LoadingOverlay.IsVisible = false;
    }
    private async void OnRemoveDeviceClicked(object? sender, EventArgs? e)
    {
        if (sender is not Microsoft.Maui.Controls.Button btn || btn.CommandParameter is not string hwidToRemove) return;
        _ = btn.ScaleToAsync(0.8, 100).ContinueWith(t => btn.ScaleToAsync(1.0, 100));
        if (hwidToRemove == DeviceHelper.GetHwid())
        {
            await DisplayAlertAsync("Внимание", "Нельзя удалить текущее устройство.", "OK").ConfigureAwait(true);
            return;
        }
        bool confirm = await DisplayAlertAsync("Отключение", "Отключить это устройство?", "Да", "Отмена").ConfigureAwait(true);
        if (!confirm) return;
        LoadingOverlay.IsVisible = true;
        var (success, error) = await _apiService.RemoveDeviceAsync(hwidToRemove).ConfigureAwait(true);
        if (success)
        {
            var item = ConnectedDevices.FirstOrDefault(d => d.Hwid == hwidToRemove);
            if (item != null) ConnectedDevices.Remove(item);
        }
        else
        {
            await DisplayAlertAsync("Ошибка", error ?? "Не удалось удалить устройство.", "OK").ConfigureAwait(true);
        }
        LoadingOverlay.IsVisible = false;
    }
}