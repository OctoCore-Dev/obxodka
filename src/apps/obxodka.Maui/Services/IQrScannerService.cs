namespace obxodka.Services;

public interface IQrScannerService
{
    public bool IsSupported { get; }
    public Task<string?> ScanQrCodeAsync();
}

public sealed class DummyQrScannerService : IQrScannerService
{
    public bool IsSupported => false;
    public Task<string?> ScanQrCodeAsync() => Task.FromResult<string?>(null);
}
