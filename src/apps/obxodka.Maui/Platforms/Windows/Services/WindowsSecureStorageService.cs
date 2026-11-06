using System.Security.Cryptography;
using Microsoft.Win32;

namespace obxodka.Platforms.Windows;

//безопасное хранилище конфиденциальных данных на базе windows dpapi и пользовательского реестра
public sealed class WindowsSecureStorageService(string subKey = "obxodka_secure") : ISecureStorageService
{
    private readonly string _subKey = $@"Software\{subKey}";

    //извлечение и расшифровка значения через dpapi
    public Task<string?> GetAsync(string key)
    {
        var sanitizedKey = SanitizeKey(key);
        using var regKey = Registry.CurrentUser.OpenSubKey($@"{_subKey}\{sanitizedKey}");
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

    //шифрование данных ключом учетной записи windows и сохранение в реестр
    public Task SetAsync(string key, string value)
    {
        var sanitizedKey = SanitizeKey(key);
        var data = System.Text.Encoding.UTF8.GetBytes(value);
        var encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        var base64 = Convert.ToBase64String(encrypted);

        using var regKey = Registry.CurrentUser.CreateSubKey($@"{_subKey}\{sanitizedKey}");
        regKey?.SetValue("Value", base64);
        return Task.CompletedTask;
    }

    //удаление конкретного ключа из защищенного хранилища
    public bool Remove(string key)
    {
        var sanitizedKey = SanitizeKey(key);
        using var regKey = Registry.CurrentUser.OpenSubKey(_subKey, true);
        if (regKey != null)
        {
            regKey.DeleteSubKeyTree(sanitizedKey, false);
            return true;
        }
        return false;
    }

    //полная очистка всех сохраненных ключей пользователя
    public void RemoveAll()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"{_subKey}", false);
        }
        catch { }
    }

    private static string SanitizeKey(string key) =>
        string.Concat(key.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.'));
}
