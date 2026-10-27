#if ANDROID
using System.Diagnostics.CodeAnalysis;
using Android.Gms.Tasks;
using Android.Runtime;
using Xamarin.Google.MLKit.Vision.Barcode.Common;
using Xamarin.Google.MLKit.Vision.CodeScanner;

namespace obxodka.Platforms.Android.Services;

[SupportedOSPlatform("android29.0")]
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
public sealed class AndroidQrScannerService : IQrScannerService
{
    public bool IsSupported => true;

    public async Task<string?> ScanQrCodeAsync()
    {
        var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("Окно приложения недоступно для запуска сканера.");
        var tcs = new TaskCompletionSource<string?>();

        try
        {
            var options = new GmsBarcodeScannerOptions.Builder()
                .SetBarcodeFormats(Barcode.FormatQrCode)
                .EnableAutoZoom()
                .Build();

            var scanner = GmsBarcodeScanning.GetClient(activity, options);

            _ = scanner.StartScan()
                .AddOnSuccessListener(new SuccessListener(tcs))
                .AddOnFailureListener(new FailureListener(tcs))
                .AddOnCanceledListener(new CanceledListener(tcs));

            return await tcs.Task;
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            if (msg.Contains("Waiting for the Barcode UI module", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("CODE_SCANNER_UNAVAILABLE", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Модуль сканера загружается из Google Play. Повторите попытку через минуту.", ex);
            }

            if (msg.Contains("API_NOT_AVAILABLE", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Google Play Services", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Сервисы Google Play недоступны на устройстве. Введите 6-значный код вручную.", ex);
            }

            throw;
        }
    }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    private sealed class SuccessListener(TaskCompletionSource<string?> tcs) : Java.Lang.Object, IOnSuccessListener
    {
        public void OnSuccess(Java.Lang.Object? result)
        {
            var barcode = result as Barcode ?? result?.JavaCast<Barcode>();
            var raw = barcode?.RawValue ?? barcode?.DisplayValue;
            _ = tcs.TrySetResult(raw);
        }
    }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    private sealed class FailureListener(TaskCompletionSource<string?> tcs) : Java.Lang.Object, IOnFailureListener
    {
        public void OnFailure(Java.Lang.Exception? e)
        {
            var msg = e?.Message ?? string.Empty;
            var error = msg.Contains("Waiting for the Barcode UI module", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("CODE_SCANNER_UNAVAILABLE", StringComparison.OrdinalIgnoreCase)
                ? "Модуль сканера загружается из Google Play. Повторите попытку через минуту."
                : (msg.Contains("API_NOT_AVAILABLE", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("Google Play Services", StringComparison.OrdinalIgnoreCase)
                    ? "Сервисы Google Play недоступны на устройстве. Введите 6-значный код вручную."
                    : (string.IsNullOrWhiteSpace(msg) ? "Ошибка запуска сканера QR-кода." : msg));

            _ = tcs.TrySetException(new InvalidOperationException(error));
        }
    }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    private sealed class CanceledListener(TaskCompletionSource<string?> tcs) : Java.Lang.Object, IOnCanceledListener
    {
        public void OnCanceled() =>
            _ = tcs.TrySetResult(null);
    }
}
#endif
