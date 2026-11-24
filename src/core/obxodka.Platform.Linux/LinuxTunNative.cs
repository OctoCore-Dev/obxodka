namespace obxodka.Platforms.Linux;

//нативные вызовы libc для работы с /dev/net/tun и привязки сокетов на linux
[SupportedOSPlatform("linux")]
internal static partial class LinuxTunNative
{
    public const string TunDevicePath = "/dev/net/tun";
    public const int OpenReadWrite = 0x0002;
    public const int OpenCloseOnExec = 0x80000;
    public const nuint TunSetIff = 0x400454CA;
    public const ushort IffTun = 0x0001;
    public const ushort IffNoPi = 0x1000;
    public const int SolSocket = 1;
    public const int SoBindToDevice = 25;
    public const int SoMark = 36;
    public const int IfReqSize = 40;
    public const int IfNameSize = 16;

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(string pathname, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    public static partial int Close(int fd);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    public static partial int Ioctl(int fd, nuint request, nint argp);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    public static partial nint Read(int fd, nint buf, nuint count);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    public static partial nint Write(int fd, nint buf, nuint count);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "setsockopt", SetLastError = true)]
    public static partial int SetSockOpt(int sockfd, int level, int optname, nint optval, uint optlen);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "geteuid")]
    public static partial uint GetEuid();

    //открытие виртуального tun устройства и получение фактического имени интерфейса
    public static (int Fd, string InterfaceName) OpenTunDevice(string requestedName)
    {
        if (!LinuxNetworkRoutingHelper.IsValidInterfaceName(requestedName))
        {
            throw new ArgumentException($"Некорректное имя TUN-интерфейса: '{requestedName}'.", nameof(requestedName));
        }

        var fd = Open(TunDevicePath, OpenReadWrite | OpenCloseOnExec);
        if (fd < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException(
                $"Не удалось открыть {TunDevicePath} (errno={err}). Проверьте наличие прав root или CAP_NET_ADMIN и модуля ядра tun.");
        }

        try
        {
            Span<byte> ifr = stackalloc byte[IfReqSize];
            ifr.Clear();

            var nameBytes = Encoding.ASCII.GetBytes(requestedName);
            var copyLen = Math.Min(nameBytes.Length, IfNameSize - 1);
            nameBytes.AsSpan(0, copyLen).CopyTo(ifr);

            var flags = (ushort)(IffTun | IffNoPi);
            _ = BitConverter.TryWriteBytes(ifr.Slice(IfNameSize, 2), flags);

            unsafe
            {
                fixed (byte* pIfr = ifr)
                {
                    if (Ioctl(fd, TunSetIff, (nint)pIfr) < 0)
                    {
                        var err = Marshal.GetLastPInvokeError();
                        throw new InvalidOperationException(
                            $"Не удалось выполнить ioctl(TUNSETIFF) для '{requestedName}' (errno={err}). Убедитесь, что есть права root или CAP_NET_ADMIN.");
                    }
                }
            }

            var nullIdx = ifr[..IfNameSize].IndexOf((byte)0);
            var actualLen = nullIdx >= 0 ? nullIdx : IfNameSize;
            var actualName = Encoding.ASCII.GetString(ifr[..actualLen]);
            return (fd, string.IsNullOrWhiteSpace(actualName) ? requestedName : actualName);
        }
        catch
        {
            _ = Close(fd);
            throw;
        }
    }

    //привязка сокета к физическому сетевому интерфейсу через so_bindtodevice
    public static bool TryBindSocketToDevice(Socket socket, string interfaceName, out int errno)
    {
        errno = 0;
        if (!LinuxNetworkRoutingHelper.IsValidInterfaceName(interfaceName))
        {
            return false;
        }

        var nameBytes = Encoding.ASCII.GetBytes(interfaceName);
        Span<byte> buf = stackalloc byte[IfNameSize];
        buf.Clear();
        nameBytes.AsSpan(0, Math.Min(nameBytes.Length, IfNameSize - 1)).CopyTo(buf);

        var handle = (int)socket.Handle;
        unsafe
        {
            fixed (byte* ptr = buf)
            {
                var res = SetSockOpt(handle, SolSocket, SoBindToDevice, (nint)ptr, (uint)(nameBytes.Length + 1));
                if (res == 0)
                {
                    return true;
                }

                errno = Marshal.GetLastPInvokeError();
                return false;
            }
        }
    }

    //установка fwmark на сокет для маршрутизации в обход туннеля
    public static bool TrySetSocketMark(Socket socket, uint mark, out int errno)
    {
        errno = 0;
        var handle = (int)socket.Handle;
        var localMark = mark;
        int res;
        unsafe
        {
            res = SetSockOpt(handle, SolSocket, SoMark, (nint)(&localMark), sizeof(uint));
        }

        if (res == 0)
        {
            return true;
        }

        errno = Marshal.GetLastPInvokeError();
        return false;
    }

    //проверка прав root или capability cap_net_admin в текущем процессе
    public static bool HasNetAdminPrivilege()
    {
        if (GetEuid() == 0)
        {
            return true;
        }

        try
        {
            const int capNetAdminBit = 12;
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (!line.StartsWith("CapEff:", StringComparison.Ordinal))
                {
                    continue;
                }

                var hex = line["CapEff:".Length..].Trim();
                if (ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var capEff))
                {
                    return (capEff & (1UL << capNetAdminBit)) != 0;
                }
            }
        }
        catch
        {
            // ignored
        }

        return false;
    }
}
