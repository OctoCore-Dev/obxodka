namespace obxodka.Services;

//контракт сервиса сканирования qr кодов камерой
public interface IQrScannerService
{
    bool IsSupported { get; }
    Task<string?> ScanQrCodeAsync();
}
