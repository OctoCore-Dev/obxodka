#if WINDOWS
using Windows.Services.Store;
using WinRT.Interop;

namespace obxodka.Services;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsAppUpdaterService : IAppUpdaterService
{
    public async Task CheckForUpdatesAsync(bool manualCheck = false)
    {
        try
        {
            var storeContext = StoreContext.GetDefault();
            if (storeContext is null)
            {
                if (manualCheck)
                {
                    await FallbackOpenStorePageAsync();
                }
                return;
            }

            var windowHandle = GetMainWindowHandle();
            if (windowHandle != IntPtr.Zero)
            {
                InitializeWithWindow.Initialize(storeContext, windowHandle);
            }

            var updates = await storeContext.GetAppAndOptionalStorePackageUpdatesAsync();
            if (updates is { Count: > 0 })
            {
                Debug.WriteLine($"[STORE UPDATE] Found {updates.Count} update(s) available in Microsoft Store.");

                var downloadOperation = storeContext.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);
                var result = await downloadOperation.AsTask();

                if (result.OverallState == StorePackageUpdateState.Completed)
                {
                    Debug.WriteLine("[STORE UPDATE] Microsoft Store update completed successfully.");
                }
                else if (result.OverallState == StorePackageUpdateState.Canceled)
                {
                    Debug.WriteLine("[STORE UPDATE] User canceled update installation.");
                }
                else if (result.OverallState != StorePackageUpdateState.Completed)
                {
                    Debug.WriteLine($"[STORE UPDATE] Status: {result.OverallState}, opening Store page...");
                    await FallbackOpenStorePageAsync();
                }
            }
            else if (manualCheck)
            {
                if (Application.Current?.Windows.Count > 0 && Application.Current.Windows[0]?.Page is not null)
                {
                    await Application.Current.Windows[0].Page!.DisplayAlertAsync("Обновления", "У вас установлена актуальная версия приложения.", "OK");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WINDOWS STORE UPDATE EXCEPTION] {ex.Message}");
            if (manualCheck)
            {
                await FallbackOpenStorePageAsync();
            }
        }
    }

    private static async Task FallbackOpenStorePageAsync()
    {
        try
        {
            _ = await Launcher.OpenAsync(new Uri("ms-windows-store://downloadsandupdates"));
        }
        catch
        {
            try
            {
                _ = await Launcher.OpenAsync(new Uri("ms-windows-store://home"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STORE LAUNCH ERROR] {ex.Message}");
            }
        }
    }

    private static IntPtr GetMainWindowHandle()
    {
        try
        {
            var window = Application.Current?.Windows is { Count: > 0 }
                ? Application.Current.Windows[0]?.Handler?.PlatformView as Microsoft.UI.Xaml.Window
                : null;
            return window is not null ? WindowNative.GetWindowHandle(window) : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }
}
#endif
