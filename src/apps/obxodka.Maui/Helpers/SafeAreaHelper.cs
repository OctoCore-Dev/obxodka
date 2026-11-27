namespace obxodka.Helpers;

//координатор безопасных отступов системных панелей для мобильных экранов
public static class SafeAreaHelper
{
    public static double TopInset { get; private set; }
    public static double BottomInset { get; private set; }
    public static double LeftInset { get; private set; }
    public static double RightInset { get; private set; }

    public static event Action<double, double>? InsetsChanged;

    //уведомление подписчиков об изменении системных отступов с защитой от повторных ложных вызовов
    public static void NotifyInsetsChanged(double top, double bottom, double left = 0, double right = 0)
    {
        //защита от повторных триггеров пересчета разметки и зацикливания верстки
        if (Math.Abs(TopInset - top) < 0.5 &&
            Math.Abs(BottomInset - bottom) < 0.5 &&
            Math.Abs(LeftInset - left) < 0.5 &&
            Math.Abs(RightInset - right) < 0.5)
        {
            return;
        }

        TopInset = top;
        BottomInset = bottom;
        LeftInset = left;
        RightInset = right;
        MainThread.BeginInvokeOnMainThread(() => InsetsChanged?.Invoke(top, bottom));
    }
}
