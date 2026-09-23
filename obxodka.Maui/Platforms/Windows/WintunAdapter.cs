namespace obxodka.Platforms.Windows;

[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed partial class WintunAdapter : IDisposable
{
    private const string DllName = "wintun.dll";
    private static readonly Guid t_defaultGuid = Guid.Parse("A1B2C3D4-E5F6-4A5B-8C9D-0E1F2A3B4C5D");

    static WintunAdapter()
    {
        NativeLibrary.SetDllImportResolver(typeof(WintunAdapter).Assembly, (libraryName, _, _) =>
        {
            if (libraryName == DllName)
            {
                var dllPath = Path.Combine(AppContext.BaseDirectory, "Engine", "wintun.dll");
                if (File.Exists(dllPath) && NativeLibrary.TryLoad(dllPath, out var handle))
                {
                    return handle;
                }
            }

            return IntPtr.Zero;
        });
    }

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr WintunCreateAdapter(string pool, string name, ref Guid requestedGuid, [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr WintunOpenAdapter(string pool, string name);

    [LibraryImport(DllName)]
    private static partial void WintunCloseAdapter(IntPtr adapter);

    [LibraryImport(DllName, SetLastError = true)]
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
    private static partial uint WaitForMultipleObjects(uint nCount, [In] IntPtr[] lpHandles, [MarshalAs(UnmanagedType.Bool)] bool bWaitAll, uint dwMilliseconds);

    private readonly Lock _syncLock = new();
    private volatile bool _isDisposed;
    private readonly ManualResetEvent _stopEvent = new(false);

    private IntPtr _adapter;
    private IntPtr _session;

    public string Name { get; }
    public string Pool { get; }

    public WintunAdapter(string name = "Obxodka", string pool = "Obxodka")
    {
        Name = name;
        Pool = pool;

        var guid = t_defaultGuid;
        _adapter = WintunCreateAdapter(pool, name, ref guid, out _);

        if (_adapter == IntPtr.Zero)
        {
            _adapter = WintunOpenAdapter(pool, name);
        }

        if (_adapter == IntPtr.Zero)
        {
            var err = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException($"Не удалось создать или открыть адаптер Wintun (Код Win32: {err}). Убедитесь, что запущен только один экземпляр программы и есть права администратора.");
        }
    }

    public void StartSession(uint capacity = 0x4000000)
    {
        lock (_syncLock)
        {
            _session = WintunStartSession(_adapter, capacity);
            if (_session == IntPtr.Zero)
            {
                var err = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException($"Не удалось запустить Wintun сессию (Код Win32: {err}).");
            }
        }
    }

    public int ReceiveBatch(PacketBatch outBatch, CancellationToken ct)
    {
        if (_isDisposed)
        {
            return 0;
        }

        IntPtr waitEvent;
        lock (_syncLock)
        {
            if (_isDisposed || _session == IntPtr.Zero)
            {
                return 0;
            }
            waitEvent = WintunGetReadWaitEvent(_session);
        }

        if (waitEvent == IntPtr.Zero)
        {
            return 0;
        }

        var handles = new[] { waitEvent, _stopEvent.SafeWaitHandle.DangerousGetHandle() };
        outBatch.Clear();
        const int maxPacketSize = 65535;

        while (!ct.IsCancellationRequested && !_isDisposed)
        {
            while (!_isDisposed)
            {
                IntPtr ptr;
                uint size;
                lock (_syncLock)
                {
                    if (_isDisposed || _session == IntPtr.Zero)
                    {
                        break;
                    }
                    ptr = WintunReceivePacket(_session, out size);
                }

                if (ptr == IntPtr.Zero)
                {
                    break;
                }

                if (size > maxPacketSize)
                {
                    lock (_syncLock)
                    {
                        if (!_isDisposed && _session != IntPtr.Zero)
                        {
                            WintunReleaseReceivePacket(_session, ptr);
                        }
                    }
                    continue;
                }

                var buf = ArrayPool<byte>.Shared.Rent((int)size);
                Marshal.Copy(ptr, buf, 0, (int)size);
                lock (_syncLock)
                {
                    if (!_isDisposed && _session != IntPtr.Zero)
                    {
                        WintunReleaseReceivePacket(_session, ptr);
                    }
                }
                outBatch.Add(buf, (int)size);

                if (outBatch.Count >= 256)
                {
                    break;
                }
            }

            if (outBatch.Count > 0 || _isDisposed || ct.IsCancellationRequested)
            {
                return outBatch.Count;
            }

            var waitResult = WaitForMultipleObjects(2, handles, false, 200);
            if (waitResult != 0 || _isDisposed)
            {
                break;
            }
        }

        return outBatch.Count;
    }

    public (byte[]? buffer, int length) ReceivePacket(CancellationToken ct)
    {
        if (_isDisposed)
        {
            return (null, 0);
        }

        IntPtr waitEvent;
        lock (_syncLock)
        {
            if (_isDisposed || _session == IntPtr.Zero)
            {
                return (null, 0);
            }
            waitEvent = WintunGetReadWaitEvent(_session);
        }

        if (waitEvent == IntPtr.Zero)
        {
            return (null, 0);
        }

        var handles = new[] { waitEvent, _stopEvent.SafeWaitHandle.DangerousGetHandle() };
        const int maxPacketSize = 65535;

        while (!ct.IsCancellationRequested && !_isDisposed)
        {
            IntPtr ptr;
            uint size;
            lock (_syncLock)
            {
                if (_isDisposed || _session == IntPtr.Zero)
                {
                    return (null, 0);
                }
                ptr = WintunReceivePacket(_session, out size);
            }

            if (ptr != IntPtr.Zero)
            {
                if (size > maxPacketSize)
                {
                    lock (_syncLock)
                    {
                        if (!_isDisposed && _session != IntPtr.Zero)
                        {
                            WintunReleaseReceivePacket(_session, ptr);
                        }
                    }
                    continue;
                }

                var data = ArrayPool<byte>.Shared.Rent((int)size);
                Marshal.Copy(ptr, data, 0, (int)size);
                lock (_syncLock)
                {
                    if (!_isDisposed && _session != IntPtr.Zero)
                    {
                        WintunReleaseReceivePacket(_session, ptr);
                    }
                }
                return (data, (int)size);
            }

            var waitResult = WaitForMultipleObjects(2, handles, false, 200);
            if (waitResult != 0 || _isDisposed)
            {
                break;
            }
        }

        return (null, 0);
    }

    public void SendPacket(byte[] data) => SendPacket(data, data.Length);

    public void SendPacket(byte[] data, int length)
    {
        if (_isDisposed)
        {
            return;
        }

        lock (_syncLock)
        {
            if (_isDisposed || _session == IntPtr.Zero)
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
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_isDisposed)
            {
                return;
            }
            _isDisposed = true;
        }

        try
        {
            _ = _stopEvent.Set();
        }
        catch { }

        Thread.Sleep(30);

        lock (_syncLock)
        {
            if (_session != IntPtr.Zero)
            {
                try
                {
                    WintunEndSession(_session);
                }
                catch { }
                _session = IntPtr.Zero;
            }

            if (_adapter != IntPtr.Zero)
            {
                try
                {
                    WintunCloseAdapter(_adapter);
                }
                catch { }
                _adapter = IntPtr.Zero;
            }
        }

        try
        {
            _stopEvent.Dispose();
        }
        catch { }
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

    public void Clear()
    {
        Array.Clear(_items, 0, Count);
        Count = 0;
    }
}
