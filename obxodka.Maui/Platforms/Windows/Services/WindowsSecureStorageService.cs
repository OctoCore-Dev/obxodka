using System.Security.Cryptography;
using Microsoft.Win32;
using obxodka.Client.Platforms;

namespace obxodka.Maui.Platforms.Windows.Services;

public sealed class WindowsSecureStorageService(string subKey = "obxodka_secure") : ISecureStorageService
{
    private readonly string _subKey = $@"Software\{subKey}";

    public Task<string?> GetAsync(string key)
    {
        using var regKey = Registry.CurrentUser.OpenSubKey($@"{_subKey}\{key}");
        var encryptedValue = regKey?.GetValue("Value") as string;
        if (string.IsNullOrEmpty(encryptedValue))
        {
            return Task.FromResult<string?>(null);
        }

        try
        {
            var data = Convert.FromBase64String(encryptedValue);
            var decrypted = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
            return Task.FromResult<string?>(System.Text.Encoding.UTF8.GetString(decrypted));
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task SetAsync(string key, string value)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(value);
        var encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        var base64 = Convert.ToBase64String(encrypted);

        using var regKey = Registry.CurrentUser.CreateSubKey($@"{_subKey}\{key}");
        regKey?.SetValue("Value", base64);
        return Task.CompletedTask;
    }

    public bool Remove(string key)
    {
        using var regKey = Registry.CurrentUser.OpenSubKey(_subKey, true);
        if (regKey != null)
        {
            regKey.DeleteSubKeyTree(key, false);
            return true;
        }
        return false;
    }

    public void RemoveAll()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"{_subKey}", false);
        }
        catch
        {
        }
    }
}
