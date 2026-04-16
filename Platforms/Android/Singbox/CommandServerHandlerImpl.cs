namespace obxodka.Platforms.Android.Singbox;
public sealed class CommandServerHandlerImpl : Java.Lang.Object, Java.Lang.Reflect.IInvocationHandler
{
    public Java.Lang.Object? Invoke(Java.Lang.Object? proxy, Java.Lang.Reflect.Method? method, Java.Lang.Object[]? args)
    {
        string name = method?.Name ?? "";
        Debug.WriteLine($"[CSH] {name}");
        return null;
    }
}
