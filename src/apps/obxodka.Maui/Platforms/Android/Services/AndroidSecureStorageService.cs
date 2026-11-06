using MauiSecureStorage = Microsoft.Maui.Storage.SecureStorage;

namespace obxodka.Platforms.Android;

//адаптер защищенного хранилища maui securestorage для android
public sealed class AndroidSecureStorageService() : ISecureStorageService
{
    //чтение зашифрованного значения по ключу
    public Task<string?> GetAsync(string key) =>
        MauiSecureStorage.Default.GetAsync(key);

    //сохранение зашифрованного значения по ключу
    public Task SetAsync(string key, string value) =>
        MauiSecureStorage.Default.SetAsync(key, value);

    //удаление ключа из защищенного хранилища
    public bool Remove(string key) =>
        MauiSecureStorage.Default.Remove(key);

    //полная очистка защищенного хранилища
    public void RemoveAll() =>
        MauiSecureStorage.Default.RemoveAll();
}
