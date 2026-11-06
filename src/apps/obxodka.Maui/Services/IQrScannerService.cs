namespace obxodka.Services;

//контракт сервиса сканирования qr кодов камерой
public interface IQrScannerService
{
    public bool IsSupported { get; }
    public Task<string?> ScanQrCodeAsync();
}
