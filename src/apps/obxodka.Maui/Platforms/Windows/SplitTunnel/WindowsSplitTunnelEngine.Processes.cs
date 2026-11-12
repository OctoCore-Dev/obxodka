using System.Buffers.Binary;

namespace obxodka.Platforms.Windows;

//определение pid процессов и сопоставление сокетов tcp/udp с исключенными приложениями
internal static partial class WindowsSplitTunnelEngine
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct MIB_TCP6ROW_OWNER_PID
    {
        public fixed byte LocalAddr[16];
        public uint LocalScopeId;
        public uint LocalPort;
        public fixed byte RemoteAddr[16];
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint LocalAddr;
        public uint LocalPort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct MIB_UDP6ROW_OWNER_PID
    {
        public fixed byte LocalAddr[16];
        public uint LocalScopeId;
        public uint LocalPort;
        public uint OwningPid;
    }

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        int tableClass,
        uint reserved);

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        int tableClass,
        uint reserved);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr OpenProcess(uint processAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, IntPtr lpExeName, ref uint lpdwSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    //сканирование активных tcp соединений процессов из белого списка и закрепление маршрутов
    public static unsafe void PinActiveProcessEndpoints(bool notify = true)
    {
        var policy = Policy;
        if (!policy.Enabled || !policy.HasBypassedExecutables || s_physicalGatewayUint == 0)
        {
            return;
        }

        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        if (size <= 0)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0) != 0)
            {
                return;
            }

            var numEntries = *(uint*)buffer;
            var rowPtr = (MIB_TCPROW_OWNER_PID*)((byte*)buffer + sizeof(uint));

            for (uint i = 0; i < numEntries; i++)
            {
                var row = rowPtr[i];
                if (row.RemoteAddr is 0 or 0x0100007Fu)
                {
                    continue;
                }

                if (TryGetProcessInfo(row.OwningPid, out var exeName, out var fullPath) &&
                    policy.IsProcessBypassed(exeName, fullPath))
                {
                    PinPhysicalHostRoute(row.RemoteAddr, exeName, notify: false);
                }
            }

            if (notify)
            {
                NotifyRoutesUpdatedAsync();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    //проверка принадлежит ли порт сокета процессу из списка исключений
    private static unsafe bool IsPortOwnedByBypassedProcess(bool isTcp, ushort localPortHostOrder, out string matchedExeName, bool isIpv6 = false)
    {
        matchedExeName = string.Empty;
        var policy = Policy;
        if (!policy.Enabled || !policy.HasBypassedExecutables)
        {
            return false;
        }

        var cacheKey = ((uint)(isTcp ? 1 : 2) << 24) | ((uint)(isIpv6 ? 1 : 0) << 16) | localPortHostOrder;
        var nowTicks = Environment.TickCount64;

        if (s_portCache.TryGetValue(cacheKey, out var cached) && nowTicks - cached.TimestampTicks < 3000)
        {
            matchedExeName = cached.ExeName;
            return cached.IsBypassed;
        }

        var portNetworkOrder = BinaryPrimitives.ReverseEndianness(localPortHostOrder);

        try
        {
            if (isTcp)
            {
                var af = isIpv6 ? AfInet6 : AfInet;
                var size = 0;
                _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TcpTableOwnerPidAll, 0);
                if (size > 0)
                {
                    size += 4096;
                    var buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        if (GetExtendedTcpTable(buffer, ref size, false, af, TcpTableOwnerPidAll, 0) == 0)
                        {
                            var numEntries = *(uint*)buffer;
                            if (!isIpv6)
                            {
                                var maxEntries = (uint)Math.Max(0, (size - sizeof(uint)) / sizeof(MIB_TCPROW_OWNER_PID));
                                var safeCount = Math.Min(numEntries, maxEntries);
                                var rowPtr = (MIB_TCPROW_OWNER_PID*)((byte*)buffer + sizeof(uint));
                                for (uint i = 0; i < safeCount; i++)
                                {
                                    var row = rowPtr[i];
                                    var rowPortHost = (ushort)(row.LocalPort & 0xFFFF);
                                    if (rowPortHost == localPortHostOrder || rowPortHost == portNetworkOrder)
                                    {
                                        if (TryGetProcessInfo(row.OwningPid, out var exe, out var fullPath) &&
                                            policy.IsProcessBypassed(exe, fullPath))
                                        {
                                            matchedExeName = exe;
                                            s_portCache[cacheKey] = (true, exe, nowTicks);
                                            return true;
                                        }
                                    }
                                }
                            }
                            else
                            {
                                var maxEntries = (uint)Math.Max(0, (size - sizeof(uint)) / sizeof(MIB_TCP6ROW_OWNER_PID));
                                var safeCount = Math.Min(numEntries, maxEntries);
                                var rowPtr6 = (MIB_TCP6ROW_OWNER_PID*)((byte*)buffer + sizeof(uint));
                                for (uint i = 0; i < safeCount; i++)
                                {
                                    var row = rowPtr6[i];
                                    var rowPortHost = (ushort)(row.LocalPort & 0xFFFF);
                                    if (rowPortHost == localPortHostOrder || rowPortHost == portNetworkOrder)
                                    {
                                        if (TryGetProcessInfo(row.OwningPid, out var exe, out var fullPath) &&
                                            policy.IsProcessBypassed(exe, fullPath))
                                        {
                                            matchedExeName = exe;
                                            s_portCache[cacheKey] = (true, exe, nowTicks);
                                            return true;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
            }
            else
            {
                var af = isIpv6 ? AfInet6 : AfInet;
                var size = 0;
                _ = GetExtendedUdpTable(IntPtr.Zero, ref size, false, af, UdpTableOwnerPid, 0);
                if (size > 0)
                {
                    size += 4096;
                    var buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        if (GetExtendedUdpTable(buffer, ref size, false, af, UdpTableOwnerPid, 0) == 0)
                        {
                            var numEntries = *(uint*)buffer;
                            if (!isIpv6)
                            {
                                var maxEntries = (uint)Math.Max(0, (size - sizeof(uint)) / sizeof(MIB_UDPROW_OWNER_PID));
                                var safeCount = Math.Min(numEntries, maxEntries);
                                var rowPtr = (MIB_UDPROW_OWNER_PID*)((byte*)buffer + sizeof(uint));
                                for (uint i = 0; i < safeCount; i++)
                                {
                                    var row = rowPtr[i];
                                    var rowPortHost = (ushort)(row.LocalPort & 0xFFFF);
                                    if (rowPortHost == localPortHostOrder || rowPortHost == portNetworkOrder)
                                    {
                                        if (TryGetProcessInfo(row.OwningPid, out var exe, out var fullPath) &&
                                            policy.IsProcessBypassed(exe, fullPath))
                                        {
                                            matchedExeName = exe;
                                            s_portCache[cacheKey] = (true, exe, nowTicks);
                                            return true;
                                        }
                                    }
                                }
                            }
                            else
                            {
                                var maxEntries = (uint)Math.Max(0, (size - sizeof(uint)) / sizeof(MIB_UDP6ROW_OWNER_PID));
                                var safeCount = Math.Min(numEntries, maxEntries);
                                var rowPtr6 = (MIB_UDP6ROW_OWNER_PID*)((byte*)buffer + sizeof(uint));
                                for (uint i = 0; i < safeCount; i++)
                                {
                                    var row = rowPtr6[i];
                                    var rowPortHost = (ushort)(row.LocalPort & 0xFFFF);
                                    if (rowPortHost == localPortHostOrder || rowPortHost == portNetworkOrder)
                                    {
                                        if (TryGetProcessInfo(row.OwningPid, out var exe, out var fullPath) &&
                                            policy.IsProcessBypassed(exe, fullPath))
                                        {
                                            matchedExeName = exe;
                                            s_portCache[cacheKey] = (true, exe, nowTicks);
                                            return true;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[SPLIT-TUNNEL PID ERROR] {ex.Message}");
        }

        s_portCache[cacheKey] = (false, string.Empty, nowTicks);
        return false;
    }

    //получение имени исполняемого файла и полного пути процесса по его pid
    private static unsafe bool TryGetProcessInfo(uint pid, out string exeName, out string fullPath)
    {
        exeName = string.Empty;
        fullPath = string.Empty;

        if (pid <= 4)
        {
            return false;
        }

        var now = Environment.TickCount64;
        if (s_pidCache.TryGetValue(pid, out var cached) && now - cached.TimestampTicks < 30000)
        {
            exeName = cached.ExeName;
            fullPath = cached.FullPath;
            return !string.IsNullOrEmpty(exeName);
        }

        var hProc = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (hProc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var chars = stackalloc char[512];
            uint len = 512;
            if (QueryFullProcessImageNameW(hProc, 0, (IntPtr)chars, ref len) && len > 0)
            {
                fullPath = new string(chars, 0, (int)len);
                exeName = Path.GetFileName(fullPath);
                s_pidCache[pid] = (exeName, fullPath, now);
                return true;
            }
        }
        finally
        {
            _ = CloseHandle(hProc);
        }

        return false;
    }
}
