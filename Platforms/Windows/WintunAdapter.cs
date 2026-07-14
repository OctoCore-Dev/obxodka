using System.Buffers;
using System.Runtime.InteropServices;
namespace obxodka.Platforms.Windows;

[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed partial class WintunAdapter : IDisposable
{
    private const string DllName = "wintun.dll";
    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr LoadLibrary(string lpFileName);
    static WintunAdapter()
    {
        var dllPath = Path.Combine(AppContext.BaseDirectory, "Engine", "wintun.dll");
        _ = LoadLibrary(dllPath);
    }
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr WintunCreateAdapter(string pool, string name, ref Guid requestedGuid, [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);
    [LibraryImport(DllName)]
    private static partial void WintunCloseAdapter(IntPtr adapter);
    [LibraryImport(DllName)]
    private static partial IntPtr WintunStartSession(IntPtr adapter, uint capacity);
    [LibraryImport(DllName)]
    private static partial void WintunEndSession(IntPtr session);
    [LibraryImport(DllName)]
    private static partial IntPtr WintunGetReadWaitEvent(IntPtr session);
    [LibraryImport(DllName)]
    private static partial IntPtr WintunReceivePacket(IntPtr session, out uint packetSize);
    [LibraryImport(DllName)]
    private static partial void WintunReleaseReceivePacket(IntPtr session, IntPtr packet);
    [LibraryImport(DllName)]
    private static partial IntPtr WintunAllocateSendPacket(IntPtr session, uint packetSize);
    [LibraryImport(DllName)]
    private static partial void WintunSendPacket(IntPtr session, IntPtr packet);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
    private IntPtr _adapter;
    private IntPtr _session;
    public string Name { get; }
    public string Pool { get; }
    public WintunAdapter(string name, string pool, Guid guid)
    {
        Name = name;
        Pool = pool;
        _adapter = WintunCreateAdapter(pool, name, ref guid, out _);
        if (_adapter == IntPtr.Zero)
        {
            throw new InvalidOperationException("Не удалось создать адаптер Wintun. ЗАПУСТИТЕ ПРОГРАММУ ОТ ИМЕНИ АДМИНИСТРАТОРА!");
        }
    }
    public void StartSession(uint capacity = 0x4000000)
    {
        _session = WintunStartSession(_adapter, capacity);
        if (_session == IntPtr.Zero)
        {
            throw new InvalidOperationException("Не удалось запустить Wintun сессию.");
        }
    }
    public int ReceiveBatch(PacketBatch outBatch, CancellationToken ct)
    {
        var waitEvent = WintunGetReadWaitEvent(_session);
        outBatch.Clear();
        const int maxPacketSize = 65535;
        while (!ct.IsCancellationRequested)
        {
            while (true)
            {
                var ptr = WintunReceivePacket(_session, out var size);
                if (ptr == IntPtr.Zero)
                {
                    break;
                }
                if (size > maxPacketSize)
                {
                    Debug.WriteLine($"[WINTUN SECURITY] Oversized packet rejected: {size} bytes");
                    WintunReleaseReceivePacket(_session, ptr);
                    continue;
                }
                var buf = ArrayPool<byte>.Shared.Rent((int)size);
                Marshal.Copy(ptr, buf, 0, (int)size);
                WintunReleaseReceivePacket(_session, ptr);
                outBatch.Add(buf, (int)size);
            }
            if (outBatch.Count > 0)
            {
                return outBatch.Count;
            }
            _ = WaitForSingleObject(waitEvent, 10);
        }
        return 0;
    }
    public (byte[]? buffer, int length) ReceivePacket(CancellationToken ct)
    {
        var waitEvent = WintunGetReadWaitEvent(_session);
        const int maxPacketSize = 65535;
        while (!ct.IsCancellationRequested)
        {
            var ptr = WintunReceivePacket(_session, out var size);
            if (ptr != IntPtr.Zero)
            {
                if (size > maxPacketSize)
                {
                    Debug.WriteLine($"[WINTUN SECURITY] Oversized packet rejected: {size} bytes");
                    WintunReleaseReceivePacket(_session, ptr);
                    continue;
                }
                var data = ArrayPool<byte>.Shared.Rent((int)size);
                Marshal.Copy(ptr, data, 0, (int)size);
                WintunReleaseReceivePacket(_session, ptr);
                return (data, (int)size);
            }
            _ = WaitForSingleObject(waitEvent, 10);
        }
        return (null, 0);
    }
    public void SendPacket(byte[] data) => SendPacket(data, data.Length);
    public void SendPacket(byte[] data, int length)
    {
        if (_session == IntPtr.Zero)
        {
            return;
        }
        var ptr = WintunAllocateSendPacket(_session, (uint)length);
        if (ptr != IntPtr.Zero)
        {
            Marshal.Copy(data, 0, ptr, length);
            WintunSendPacket(_session, ptr);
        }
    }
    public void Dispose()
    {
        if (_session != IntPtr.Zero)
        {
            WintunEndSession(_session);
            _session = IntPtr.Zero;
        }
        if (_adapter != IntPtr.Zero)
        {
            WintunCloseAdapter(_adapter);
            _adapter = IntPtr.Zero;
        }
    }
}
internal sealed class PacketBatch
{
    private readonly (byte[] buffer, int length)[] _items = new (byte[], int)[256];
    public int Count { get; private set; }
    public void Add(byte[] buf, int len)
    {
        if (Count < _items.Length)
        {
            _items[Count++] = (buf, len);
        }
        else
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }
    public (byte[] buffer, int length) this[int i] => _items[i];
    public void Clear() => Count = 0;
}
