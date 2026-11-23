using Path = System.IO.Path;

namespace obxodka.Platforms.Windows;

//нативная обертка для создания и управления сессиями сетевого адаптера wintun
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed partial class WintunAdapter : IDisposable
{
    private const string DllName = "wintun.dll";
    private static readonly Guid s_defaultGuid = Guid.Parse("A1B2C3D4-E5F6-4A5B-8C9D-0E1F2A3B4C5D");

    private readonly Lock _rxLock = new();
    private readonly ManualResetEvent _stopEvent = new(false);
    private readonly Lock _txLock = new();

    private IntPtr _adapter;
    private volatile bool _isDisposed;
    private IntPtr _session;

    static WintunAdapter()
    {
        NativeLibrary.SetDllImportResolver(typeof(WintunAdapter).Assembly, (libraryName, _, _) =>
        {
            if (libraryName != DllName)
            {
                return IntPtr.Zero;
            }

            var dllPath = Path.Combine(AppContext.BaseDirectory, "Engine", "wintun.dll");
            return File.Exists(dllPath) && NativeLibrary.TryLoad(dllPath, out var handle) ? handle : IntPtr.Zero;
        });
    }

    public WintunAdapter(string name = "Obxodka", string pool = "Obxodka")
    {
        Name = name;
        Pool = pool;

        _adapter = WintunOpenAdapter(name);
        if (_adapter != IntPtr.Zero)
        {
            RefreshInterfaceIdentity();
            AppLogger.Log($"[WINTUN] Successfully opened existing Wintun adapter '{name}' (ifIndex={InterfaceIndex}).");
            return;
        }

        var openErr = Marshal.GetLastPInvokeError();

        var freshGuid = Guid.NewGuid();
        _adapter = WintunCreateAdapter(name, pool, ref freshGuid);
        if (_adapter != IntPtr.Zero)
        {
            RefreshInterfaceIdentity();
            AppLogger.Log($"[WINTUN] Successfully created new Wintun adapter '{name}' (GUID: {freshGuid}, ifIndex={InterfaceIndex}).");
            return;
        }

        var createErrFresh = Marshal.GetLastPInvokeError();
        AppLogger.LogWarning($"[WINTUN] Create with fresh GUID failed (Win32 {createErrFresh}). Trying default GUID...");

        var defaultGuid = s_defaultGuid;
        _adapter = WintunCreateAdapter(name, pool, ref defaultGuid);
        if (_adapter != IntPtr.Zero)
        {
            RefreshInterfaceIdentity();
            AppLogger.Log($"[WINTUN] Successfully created Wintun adapter '{name}' with default GUID (ifIndex={InterfaceIndex}).");
            return;
        }

        var createErrDefault = Marshal.GetLastPInvokeError();
        AppLogger.LogError(
            $"[WINTUN FATAL] Failed to create or open Wintun adapter '{name}'. OpenErr={openErr}, CreateErrFresh={createErrFresh}, CreateErrDefault={createErrDefault}");
        throw new InvalidOperationException(
            $"Не удалось создать или открыть адаптер Wintun (Код Win32: {createErrFresh}, OpenErr: {openErr}). Убедитесь, что запущен только один экземпляр программы и есть права администратора.");
    }

    public string Name { get; }
    public string Pool { get; }
    public ulong InterfaceLuid { get; private set; }
    public uint InterfaceIndex { get; private set; }
    public bool IsSessionActive => _session != IntPtr.Zero;
    public bool IsDisposed => _isDisposed || _adapter == IntPtr.Zero;

    //чтение системного luid и индекса интерфейса напрямую из wintun без netsh/powershell
    private void RefreshInterfaceIdentity()
    {
        if (_adapter == IntPtr.Zero)
        {
            return;
        }

        WintunGetAdapterLuid(_adapter, out var luid);
        InterfaceLuid = luid;
        if (Win32NetworkHelper.TryGetInterfaceIndex(luid, out var idx))
        {
            InterfaceIndex = idx;
        }
    }

    //освобождение ресурсов адаптера
    public void Dispose()
    {
        lock (_rxLock)
        {
            lock (_txLock)
            {
                if (_isDisposed)
                {
                    return;
                }

                _isDisposed = true;
            }
        }

        EndSession();

        Thread.Sleep(30);

        lock (_rxLock)
        {
            lock (_txLock)
            {
                if (_adapter != IntPtr.Zero)
                {
                    try
                    {
                        WintunCloseAdapter(_adapter);
                    }
                    catch
                    {
                        // ignored
                    }

                    _adapter = IntPtr.Zero;
                }

                try
                {
                    _stopEvent.Dispose();
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr WintunCreateAdapter(string name, string tunnelType, ref Guid requestedGuid);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr WintunOpenAdapter(string name);

    [LibraryImport(DllName)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void WintunCloseAdapter(IntPtr adapter);

    [LibraryImport(DllName, EntryPoint = "WintunGetAdapterLUID")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void WintunGetAdapterLuid(IntPtr adapter, out ulong luid);

    [LibraryImport(DllName, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr WintunStartSession(IntPtr adapter, uint capacity);

    [LibraryImport(DllName)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void WintunEndSession(IntPtr session);

    [LibraryImport(DllName)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr WintunGetReadWaitEvent(IntPtr session);

    [LibraryImport(DllName)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr WintunReceivePacket(IntPtr session, out uint packetSize);

    [LibraryImport(DllName)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void WintunReleaseReceivePacket(IntPtr session, IntPtr packet);

    [LibraryImport(DllName)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr WintunAllocateSendPacket(IntPtr session, uint packetSize);

    [LibraryImport(DllName)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void WintunSendPacket(IntPtr session, IntPtr packet);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaitForMultipleObjects(uint nCount, [In] IntPtr[] lpHandles, [MarshalAs(UnmanagedType.Bool)] bool bWaitAll,
        uint dwMilliseconds);

    //запуск сессии обмена пакетами
    public void StartSession(uint capacity = 0x800000)
    {
        lock (_rxLock)
        {
            lock (_txLock)
            {
                ObjectDisposedException.ThrowIf(_isDisposed || _adapter == IntPtr.Zero, this);

                if (_session != IntPtr.Zero)
                {
                    return;
                }

                try
                {
                    _ = _stopEvent.Reset();
                }
                catch
                {
                    // ignored
                }

                _session = WintunStartSession(_adapter, capacity);
                if (_session != IntPtr.Zero)
                {
                    return;
                }

                var err = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException($"Не удалось запустить Wintun сессию (Код Win32: {err}).");
            }
        }
    }

    //завершение активной сессии
    public void EndSession()
    {
        try
        {
            _ = _stopEvent.Set();
        }
        catch
        {
            // ignored
        }

        lock (_rxLock)
        {
            lock (_txLock)
            {
                if (_session == IntPtr.Zero)
                {
                    return;
                }

                try
                {
                    WintunEndSession(_session);
                }
                catch
                {
                    // ignored
                }

                _session = IntPtr.Zero;
            }
        }
    }

    //пакетное получение данных из интерфейса wintun
    public int ReceiveBatch(PacketBatch outBatch, CancellationToken ct)
    {
        if (_isDisposed || _session == IntPtr.Zero)
        {
            return 0;
        }

        IntPtr waitEvent;
        lock (_rxLock)
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

        var stopSafeHandle = _stopEvent.SafeWaitHandle;
        var stopRefAdded = false;
        try
        {
            stopSafeHandle.DangerousAddRef(ref stopRefAdded);
            var handles = new[] { waitEvent, stopSafeHandle.DangerousGetHandle() };
            outBatch.Clear();
            const int maxPacketSize = 65535;

            while (!ct.IsCancellationRequested && !_isDisposed && _session != IntPtr.Zero)
            {
                lock (_rxLock)
                {
                    if (_isDisposed || _session == IntPtr.Zero)
                    {
                        break;
                    }

                    while (!_isDisposed && _session != IntPtr.Zero)
                    {
                        var ptr = WintunReceivePacket(_session, out var size);
                        if (ptr == IntPtr.Zero)
                        {
                            break;
                        }

                        if (size > maxPacketSize)
                        {
                            WintunReleaseReceivePacket(_session, ptr);
                            continue;
                        }

                        var buf = ArrayPool<byte>.Shared.Rent((int)size);
                        Marshal.Copy(ptr, buf, 0, (int)size);
                        WintunReleaseReceivePacket(_session, ptr);
                        outBatch.Add(buf, (int)size);

                        if (outBatch.Count >= 256)
                        {
                            break;
                        }
                    }
                }

                if (outBatch.Count > 0 || _isDisposed || _session == IntPtr.Zero || ct.IsCancellationRequested)
                {
                    return outBatch.Count;
                }

                uint waitResult;
                try
                {
                    waitResult = WaitForMultipleObjects(2, handles, false, 200);
                }
                catch
                {
                    break;
                }

                if (waitResult != 0 || _isDisposed || _session == IntPtr.Zero)
                {
                    break;
                }
            }

            return outBatch.Count;
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (stopRefAdded)
            {
                stopSafeHandle.DangerousRelease();
            }
        }
    }

    //отправка пакета в сетевой стек windows
    public void SendPacket(byte[] data) => SendPacket(data, data.Length);

    //отправка пакета заданной длины в сетевой стек windows
    public void SendPacket(byte[] data, int length)
    {
        if (_isDisposed || _session == IntPtr.Zero)
        {
            return;
        }

        lock (_txLock)
        {
            if (_isDisposed || _session == IntPtr.Zero)
            {
                return;
            }

            var ptr = WintunAllocateSendPacket(_session, (uint)length);
            if (ptr == IntPtr.Zero)
            {
                return;
            }

            Marshal.Copy(data, 0, ptr, length);
            WintunSendPacket(_session, ptr);
        }
    }
}

//пакетный буфер для извлечения кадров wintun без повторных аллокаций
internal sealed class PacketBatch
{
    private readonly (byte[] buffer, int length)[] _items = new (byte[], int)[256];
    public int Count { get; private set; }

    public (byte[] buffer, int length) this[int i] => _items[i];

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

    public void Clear()
    {
        Array.Clear(_items, 0, Count);
        Count = 0;
    }
}
