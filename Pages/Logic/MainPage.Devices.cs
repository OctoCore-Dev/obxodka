namespace obxodka.Pages;

internal sealed partial class MainPage
{
    private async Task LoadDevicesAsync()
    {
        DevicesLoadingOverlay.IsVisible = true;
        DevicesList.IsVisible = false;
        ConnectedDevices.Clear();
        DevicesErrorLabel.Opacity = 0;

        try
        {
            var (success, devices, error) = await _apiService.GetDevicesAsync();
            if (success && devices is not null)
            {
                foreach (var d in devices)
                {
                    ConnectedDevices.Add(d);
                }
            }
            else if (!string.IsNullOrEmpty(error))
            {
                await ShowDevicesErrorAsync(ApiErrorHandler.ParseGeneralError(error, "Не удалось загрузить список устройств."));
            }
        }
        catch
        {
            await ShowDevicesErrorAsync("Проблема с соединением.");
        }
        finally
        {
            DevicesLoadingOverlay.IsVisible = false;
            DevicesList.IsVisible = true;
        }
    }

    private async void OnRemoveDeviceClickedAsync(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string hwidToRemove } btn)
        {
            return;
        }

        _ = btn.BounceClickAsync();

        if (hwidToRemove == DeviceHelper.GetHwid())
        {
            await ShowDevicesErrorAsync("Нельзя удалить текущее устройство.");
            return;
        }

        var confirm = await DisplayAlertAsync("Отключение", "Отключить это устройство?", "Да", "Отмена");
        if (!confirm)
        {
            return;
        }

        DevicesLoadingOverlay.IsVisible = true;
        try
        {
            var (success, error) = await _apiService.RemoveDeviceAsync(hwidToRemove);
            if (success)
            {
                var item = ConnectedDevices.FirstOrDefault(d => d.Hwid == hwidToRemove);
                if (item is not null)
                {
                    _ = ConnectedDevices.Remove(item);
                }
            }
            else
            {
                await ShowDevicesErrorAsync(ApiErrorHandler.ParseGeneralError(error, "Не удалось удалить устройство."));
            }
        }
        catch
        {
            await ShowDevicesErrorAsync("Проблема с соединением.");
        }
        finally
        {
            DevicesLoadingOverlay.IsVisible = false;
        }
    }

    private async Task ShowDevicesErrorAsync(string msg)
    {
        DevicesErrorLabel.Text = msg;
        _ = DevicesErrorLabel.FadeToAsync(1, 200);
        await TabContentDevices.ShakeErrorAsync();
    }
}
