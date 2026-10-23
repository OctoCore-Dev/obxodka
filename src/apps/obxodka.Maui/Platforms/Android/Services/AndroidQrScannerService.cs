#if ANDROID
using Android.Gms.Tasks;
using Xamarin.Google.MLKit.Vision.Barcode.Common;
using Xamarin.Google.MLKit.Vision.CodeScanner;

namespace obxodka.Platforms.Android.Services;

[SupportedOSPlatform("android29.0")]
public sealed class AndroidQrScannerService : IQrScannerService
{
    public bool IsSupported => true;

    public async Task<string?> ScanQrCodeAsync()
    {
        var activity = Platform.CurrentActivity;
        if (activity == null)
        {
            return null;
        }

        var tcs = new TaskCompletionSource<string?>();

        try
        {
            var options = new GmsBarcodeScannerOptions.Builder()
                .SetBarcodeFormats(Barcode.FormatQrCode)
                .Build();

            var scanner = GmsBarcodeScanning.GetClient(activity, options);

            _ = scanner.StartScan()
                .AddOnSuccessListener(new SuccessListener(tcs))
                .AddOnFailureListener(new FailureListener(tcs))
                .AddOnCanceledListener(new CanceledListener(tcs));

            return await tcs.Task;
        }
        catch
        {
            return null;
        }
    }

    private sealed class SuccessListener(TaskCompletionSource<string?> tcs) : Java.Lang.Object, IOnSuccessListener
    {
        public void OnSuccess(Java.Lang.Object? result) => _ = result is Barcode barcode ? tcs.TrySetResult(barcode.RawValue) : tcs.TrySetResult(null);
    }

    private sealed class FailureListener(TaskCompletionSource<string?> tcs) : Java.Lang.Object, IOnFailureListener
    {
        public void OnFailure(Java.Lang.Exception? e) =>
            _ = tcs.TrySetResult(null);
    }

    private sealed class CanceledListener(TaskCompletionSource<string?> tcs) : Java.Lang.Object, IOnCanceledListener
    {
        public void OnCanceled() =>
            _ = tcs.TrySetResult(null);
    }
}
#endif
