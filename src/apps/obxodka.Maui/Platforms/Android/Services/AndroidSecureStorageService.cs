using obxodka.Client.Platforms;
using MauiSecureStorage = Microsoft.Maui.Storage.SecureStorage;

namespace obxodka.Maui.Platforms.Android.Services;

public sealed class AndroidSecureStorageService() : ISecureStorageService
{
    public Task<string?> GetAsync(string key) =>
        MauiSecureStorage.Default.GetAsync(key);

    public Task SetAsync(string key, string value) =>
        MauiSecureStorage.Default.SetAsync(key, value);

    public bool Remove(string key) =>
        MauiSecureStorage.Default.Remove(key);

    public void RemoveAll() =>
        MauiSecureStorage.Default.RemoveAll();
}
