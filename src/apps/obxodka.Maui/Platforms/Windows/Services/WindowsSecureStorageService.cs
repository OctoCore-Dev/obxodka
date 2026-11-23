using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace obxodka.Platforms.Windows;

//безопасное хранилище конфиденциальных данных на базе windows dpapi и пользовательского реестра
public sealed class WindowsSecureStorageService(string subKey = "obxodka_secure") : ISecureStorageService
{
    private static readonly byte[] s_entropy = "OBXODKA-DPAPI-V1"u8.ToArray();
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
            byte[] decrypted;
            try
            {
                decrypted = ProtectedData.Unprotect(data, s_entropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                //обратная совместимость со значениями, сохраненными без энтропии
                decrypted = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
            }

            return Task.FromResult<string?>(Encoding.UTF8.GetString(decrypted));
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
        var data = Encoding.UTF8.GetBytes(value);
        var encrypted = ProtectedData.Protect(data, s_entropy, DataProtectionScope.CurrentUser);
        var base64 = Convert.ToBase64String(encrypted);

        using var regKey = Registry.CurrentUser.CreateSubKey($@"{_subKey}\{sanitizedKey}");
        regKey.SetValue("Value", base64);
        return Task.CompletedTask;
    }

    //удаление конкретного ключа из защищенного хранилища
    public bool Remove(string key)
    {
        var sanitizedKey = SanitizeKey(key);
        using var regKey = Registry.CurrentUser.OpenSubKey(_subKey, true);
        if (regKey == null)
        {
            return false;
        }

        regKey.DeleteSubKeyTree(sanitizedKey, false);
        return true;
    }

    //полная очистка всех сохраненных ключей пользователя
    public void RemoveAll()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"{_subKey}", false);
        }
        catch
        {
            // ignored
        }
    }

    private static string SanitizeKey(string key)
    {
        var clean = string.Concat(key.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.'));
        return string.IsNullOrEmpty(clean) ? "_empty_" : clean;
    }
}
