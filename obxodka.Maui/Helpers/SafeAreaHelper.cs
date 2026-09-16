namespace obxodka.Helpers;

public static class SafeAreaHelper
{
    public static double TopInset { get; private set; }
    public static double BottomInset { get; private set; }

    public static event Action<double, double>? InsetsChanged;

    public static void NotifyInsetsChanged(double top, double bottom)
    {
        TopInset = top;
        BottomInset = bottom;
        MainThread.BeginInvokeOnMainThread(() => InsetsChanged?.Invoke(top, bottom));
    }
}
