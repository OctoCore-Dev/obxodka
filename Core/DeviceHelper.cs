namespace obxodka.Models;
internal static class DeviceHelper
{
    public static string GetHwid()
    {
#if WINDOWS
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (key != null)
            {
                var guid = key.GetValue("MachineGuid")?.ToString();
            }
        }
        catch (IOException ioEx)
        {
            Debug.WriteLine($"[AUTH I/O ERROR]: {ioEx.Message}");
            throw;
        }
        catch (UnauthorizedAccessException authEx)
        {
            Debug.WriteLine($"[AUTH ACCESS ERROR]: {authEx.Message}");
            throw;
        }
        catch (JsonException jsonEx)
        {
            Debug.WriteLine($"[AUTH JSON ERROR]: {jsonEx.Message}");
        }
        catch (Exception)
        {
            throw;
        }
        return Environment.MachineName + "_" + Environment.UserName;
#elif ANDROID
        try
        {
            var context = Android.App.Application.Context;
            string? androidId = Android.Provider.Settings.Secure.GetString(context.ContentResolver, Android.Provider.Settings.Secure.AndroidId);
            if (!string.IsNullOrEmpty(androidId)) return androidId;
        }
        catch { }
        var storedId = Preferences.Default.Get("hwid_cache", "");
        if (string.IsNullOrEmpty(storedId))
        {
            storedId = Guid.NewGuid().ToString();
            Preferences.Default.Set("hwid_cache", storedId);
        }
        return storedId;
#else
        return Guid.NewGuid().ToString();
#endif
    }
    public static string GetDeviceName()
    {
        return Microsoft.Maui.Devices.DeviceInfo.Current.Name;
    }
}
