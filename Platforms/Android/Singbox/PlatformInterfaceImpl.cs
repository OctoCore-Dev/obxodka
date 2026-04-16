namespace obxodka.Platforms.Android.Singbox;
public sealed class PlatformInterfaceImpl : Java.Lang.Object, Java.Lang.Reflect.IInvocationHandler
{
    private readonly int _fd;
    public PlatformInterfaceImpl(int fd) { _fd = fd; }
    public Java.Lang.Object? Invoke(Java.Lang.Object? proxy, Java.Lang.Reflect.Method? method, Java.Lang.Object[]? args)
    {
        string name = method?.Name ?? "";
        if (name != "findConnectionOwner" && name != "readWIFIState")
            Debug.WriteLine($"[PI] {name}");
        switch (name)
        {
            case "usePlatformAutoDetectInterfaceControl":
                return Java.Lang.Boolean.False;
            case "useProcFS":
                return Java.Lang.Boolean.False;
            case "underNetworkExtension":
                return Java.Lang.Boolean.False;
            case "includeAllNetworks":
                return Java.Lang.Boolean.False;
            case "openTun":
                Debug.WriteLine($"[PI] openTun → FD: {_fd}");
                return Java.Lang.Integer.ValueOf(_fd);
            case "findConnectionOwner":
                try
                {
                    var cl = global::Android.App.Application.Context.ClassLoader!;
                    var coClass = cl.LoadClass("io.nekohasekai.libbox.ConnectionOwner")!;
                    return coClass.GetConstructor(Array.Empty<Java.Lang.Class>()).NewInstance();
                }
                catch { return null; }
            default:
                return null;
        }
    }
}