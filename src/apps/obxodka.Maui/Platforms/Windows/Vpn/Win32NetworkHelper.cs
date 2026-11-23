namespace obxodka.Platforms.Windows;

//нативный win32 слой управления сетевыми интерфейсами, ip, dns и таблицей маршрутизации (iphlpapi + dnsapi)
[SupportedOSPlatform("windows10.0.19041.0")]
internal static partial class Win32NetworkHelper
{
    internal const ushort AfInet = 2;
    internal const ushort AfInet6 = 23;
    private const ulong DnsSettingIpv6 = 0x0001;
    private const ulong DnsSettingNameServer = 0x0002;
    private const int ErrorObjectAlreadyExists = 5010;

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FlushDnsCache();

    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint ConvertInterfaceLuidToIndex(in ulong interfaceLuid, out uint interfaceIndex);

    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint ConvertInterfaceIndexToLuid(uint interfaceIndex, out ulong interfaceLuid);

    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint ConvertInterfaceLuidToGuid(in ulong interfaceLuid, out Guid interfaceGuid);

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int GetBestRoute(uint dwDestAddr, uint dwSourceAddr, out MibIpForwardRow pBestRoute);

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int DeleteIpForwardEntry(ref MibIpForwardRow pRoute);

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetIpForwardTable(IntPtr pIpForwardTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder);

    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void InitializeIpForwardEntry(out MibIpForwardRow2 row);

    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint CreateIpForwardEntry2(ref MibIpForwardRow2 row);

    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint SetIpForwardEntry2(ref MibIpForwardRow2 row);

    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint DeleteIpForwardEntry2(ref MibIpForwardRow2 row);

    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetIpForwardTable2(ushort family, out IntPtr table);

    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void InitializeUnicastIpAddressEntry(out MibUnicastIpAddressRow row);

    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint CreateUnicastIpAddressEntry(ref MibUnicastIpAddressRow row);

    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint DeleteUnicastIpAddressEntry(ref MibUnicastIpAddressRow row);

    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetUnicastIpAddressTable(ushort family, out IntPtr table);

    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void InitializeIpInterfaceEntry(out MibIpInterfaceRow row);

    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetIpInterfaceEntry(ref MibIpInterfaceRow row);

    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint SetIpInterfaceEntry(ref MibIpInterfaceRow row);

    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetInterfaceDnsSettings(Guid interfaceGuid, ref DnsInterfaceSettings settings);

    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint SetInterfaceDnsSettings(Guid interfaceGuid, in DnsInterfaceSettings settings);

    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void FreeInterfaceDnsSettings(ref DnsInterfaceSettings settings);

    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void FreeMibTable(IntPtr memory);

    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        int tableClass,
        uint reserved);

    //преобразование interface luid в системный индекс
    public static bool TryGetInterfaceIndex(ulong interfaceLuid, out uint ifIndex)
    {
        ifIndex = 0;
        return interfaceLuid != 0 && ConvertInterfaceLuidToIndex(in interfaceLuid, out ifIndex) == 0 && ifIndex > 0;
    }

    //преобразование системного индекса в interface luid
    public static bool TryGetInterfaceLuid(uint ifIndex, out ulong interfaceLuid)
    {
        interfaceLuid = 0;
        return ifIndex > 0 && ConvertInterfaceIndexToLuid(ifIndex, out interfaceLuid) == 0 && interfaceLuid != 0;
    }

    //преобразование interface luid в guid адаптера
    public static bool TryGetInterfaceGuid(ulong interfaceLuid, out Guid interfaceGuid)
    {
        interfaceGuid = Guid.Empty;
        return interfaceLuid != 0 && ConvertInterfaceLuidToGuid(in interfaceLuid, out interfaceGuid) == 0 && interfaceGuid != Guid.Empty;
    }

    //преобразование индекса интерфейса напрямую в guid адаптера
    public static bool TryGetInterfaceGuidByIndex(int ifIndex, out Guid interfaceGuid)
    {
        interfaceGuid = Guid.Empty;
        return ifIndex > 0 && TryGetInterfaceLuid((uint)ifIndex, out var luid) && TryGetInterfaceGuid(luid, out interfaceGuid);
    }

    //парсинг ipv4 строки в сетевой порядок байт
    private static bool TryParseIpv4NetworkOrder(string ip, out uint addr)
    {
        addr = 0;
        if (!IPAddress.TryParse(ip, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[4];
        if (!parsed.TryWriteBytes(bytes, out _))
        {
            return false;
        }

        addr = MemoryMarshal.Read<uint>(bytes);
        return true;
    }

    //добавление или обновление ipv4 маршрута (с записью примененной структуры)
    public static int AddIpv4Route(
        uint destNetOrder,
        uint maskNetOrder,
        uint nextHopNetOrder,
        uint ifIndex,
        uint metric,
        out MibIpForwardRow appliedRow)
    {
        var appliedMetric = Math.Max(metric, 1u);
        appliedRow = new MibIpForwardRow
        {
            DwForwardDest = destNetOrder,
            DwForwardMask = maskNetOrder,
            DwForwardPolicy = 0,
            DwForwardNextHop = nextHopNetOrder,
            DwForwardIfIndex = ifIndex,
            DwForwardType = nextHopNetOrder == 0 ? 3u : 4u,
            DwForwardProto = 3,
            DwForwardAge = 0,
            DwForwardNextHopAS = 0,
            DwForwardMetric1 = appliedMetric,
            DwForwardMetric2 = uint.MaxValue,
            DwForwardMetric3 = uint.MaxValue,
            DwForwardMetric4 = uint.MaxValue,
            DwForwardMetric5 = uint.MaxValue
        };

        InitializeIpForwardEntry(out var row2);
        if (ifIndex > 0)
        {
            row2.InterfaceIndex = ifIndex;
            if (TryGetInterfaceLuid(ifIndex, out var luid))
            {
                row2.InterfaceLuid = luid;
            }
        }

        row2.DestFamily = AfInet;
        row2.DestAddrV4 = destNetOrder;
        row2.DestPrefixLength = (byte)System.Numerics.BitOperations.PopCount(maskNetOrder);
        row2.NextHopFamily = AfInet;
        row2.NextHopAddrV4 = nextHopNetOrder;
        row2.Metric = appliedMetric;
        row2.Protocol = 3;

        var status = CreateIpForwardEntry2(ref row2);
        if (status == ErrorObjectAlreadyExists)
        {
            status = SetIpForwardEntry2(ref row2);
        }

        return (int)status;
    }

    //добавление или обновление ipv4 маршрута по строковым адресам
    public static int AddIpv4Route(string destIp, string maskIp, string nextHopIp, uint ifIndex, uint metric = 1) =>
        TryParseIpv4NetworkOrder(destIp, out var dest) &&
        TryParseIpv4NetworkOrder(maskIp, out var mask) &&
        TryParseIpv4NetworkOrder(nextHopIp, out var nextHop)
            ? AddIpv4Route(dest, mask, nextHop, ifIndex, metric, out _)
            : -1;

    //попытка добавления ipv4 маршрута с набором запасных метрик
    public static int AddIpv4RouteWithMetricFallback(
        uint destNetOrder,
        uint maskNetOrder,
        uint nextHopNetOrder,
        uint ifIndex,
        ReadOnlySpan<uint> candidateMetrics,
        out MibIpForwardRow appliedRow)
    {
        appliedRow = default;
        var status = -1;
        foreach (var metric in candidateMetrics)
        {
            status = AddIpv4Route(destNetOrder, maskNetOrder, nextHopNetOrder, ifIndex, metric, out appliedRow);
            if (status == 0)
            {
                return 0;
            }
        }

        return status;
    }

    //удаление всех ipv4 маршрутов из span за один проход системной таблицы
    public static void DeleteIpv4Routes(params ReadOnlySpan<(string Dest, string Mask)> targets)
    {
        var parsedTargets = new HashSet<(uint Dest, uint Mask)>();
        foreach (var (destStr, maskStr) in targets)
        {
            if (TryParseIpv4NetworkOrder(destStr, out var d) && TryParseIpv4NetworkOrder(maskStr, out var m))
            {
                _ = parsedTargets.Add((d, m));
            }
        }

        DeleteParsedIpv4Routes(parsedTargets);
    }

    //удаление всех ipv4 маршрутов из перечисления за один проход системной таблицы
    public static void DeleteIpv4Routes(IEnumerable<(string Dest, string Mask)> targets)
    {
        var parsedTargets = new HashSet<(uint Dest, uint Mask)>();
        foreach (var (destStr, maskStr) in targets)
        {
            if (TryParseIpv4NetworkOrder(destStr, out var d) && TryParseIpv4NetworkOrder(maskStr, out var m))
            {
                _ = parsedTargets.Add((d, m));
            }
        }

        DeleteParsedIpv4Routes(parsedTargets);
    }

    private static unsafe void DeleteParsedIpv4Routes(HashSet<(uint Dest, uint Mask)> parsedTargets)
    {
        if (parsedTargets.Count == 0)
        {
            return;
        }

        var size = 0;
        _ = GetIpForwardTable(IntPtr.Zero, ref size, false);
        if (size <= 0)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpForwardTable(buffer, ref size, false) != 0)
            {
                return;
            }

            var count = *(int*)buffer;
            var rows = (MibIpForwardRow*)((byte*)buffer + sizeof(int));
            for (var i = 0; i < count; i++)
            {
                var row = rows[i];
                if (parsedTargets.Contains((row.DwForwardDest, row.DwForwardMask)))
                {
                    _ = DeleteIpForwardEntry(ref row);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    //поиск ipv4 маршрута по умолчанию (0.0.0.0/0) с минимальной метрикой без powershell
    public static unsafe bool TryGetLowestMetricDefaultRouteV4(uint excludedIfIndex, out string gateway, out int ifIndex)
    {
        gateway = string.Empty;
        ifIndex = 0;

        var size = 0;
        _ = GetIpForwardTable(IntPtr.Zero, ref size, true);
        if (size <= 0)
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpForwardTable(buffer, ref size, true) != 0)
            {
                return false;
            }

            var count = *(int*)buffer;
            var rows = (MibIpForwardRow*)((byte*)buffer + sizeof(int));
            var bestMetric = uint.MaxValue;
            uint bestNextHop = 0;
            uint bestIf = 0;

            Span<byte> gwBytes = stackalloc byte[4];
            for (var i = 0; i < count; i++)
            {
                var row = rows[i];
                if (row.DwForwardDest != 0 || row.DwForwardMask != 0 || row.DwForwardNextHop == 0)
                {
                    continue;
                }

                if (excludedIfIndex > 0 && row.DwForwardIfIndex == excludedIfIndex)
                {
                    continue;
                }

                if (row.DwForwardMetric1 < bestMetric)
                {
                    bestMetric = row.DwForwardMetric1;
                    bestNextHop = row.DwForwardNextHop;
                    bestIf = row.DwForwardIfIndex;
                }
            }

            if (bestNextHop == 0 || bestIf == 0)
            {
                return false;
            }

            MemoryMarshal.Write(gwBytes, in bestNextHop);
            gateway = new IPAddress(gwBytes).ToString();
            ifIndex = (int)bestIf;
            return gateway != "0.0.0.0";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    //добавление или обновление ipv6 маршрута
    public static unsafe uint AddIpv6Route(string prefixIp, byte prefixLength, ulong interfaceLuid, uint ifIndex, uint metric)
    {
        if (!IPAddress.TryParse(prefixIp, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return 87;
        }

        InitializeIpForwardEntry(out var row);
        row.InterfaceLuid = interfaceLuid;
        row.InterfaceIndex = ifIndex;
        row.DestFamily = AfInet6;
        row.DestPrefixLength = prefixLength;
        row.NextHopFamily = AfInet6;
        row.Metric = metric;
        row.Protocol = 3;

        Span<byte> addrBytes = stackalloc byte[16];
        _ = parsed.TryWriteBytes(addrBytes, out _);
        for (var i = 0; i < 16; i++)
        {
            row.DestAddrV6[i] = addrBytes[i];
        }

        var status = CreateIpForwardEntry2(ref row);
        if (status == ErrorObjectAlreadyExists)
        {
            status = SetIpForwardEntry2(ref row);
        }

        return status;
    }

    //удаление ipv6 маршрутов за один проход системной таблицы (по индексу интерфейса или фильтру префиксов)
    public static unsafe void DeleteIpv6Routes(uint? filterIfIndex, params (string Prefix, byte PrefixLen)[] targets)
    {
        if (GetIpForwardTable2(AfInet6, out var tablePtr) != 0 || tablePtr == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var parsedTargets = new List<(byte[] Addr, byte PrefixLen)>(targets.Length);
            foreach (var (prefix, len) in targets)
            {
                if (IPAddress.TryParse(prefix, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    parsedTargets.Add((ip.GetAddressBytes(), len));
                }
            }

            if (parsedTargets.Count == 0)
            {
                return;
            }

            var count = *(uint*)tablePtr;
            var rows = (MibIpForwardRow2*)((byte*)tablePtr + 8);
            Span<byte> rowAddr = stackalloc byte[16];
            for (var i = 0; i < count; i++)
            {
                var row = rows[i];
                if (row.DestFamily != AfInet6)
                {
                    continue;
                }

                if (filterIfIndex is { } targetIdx && row.InterfaceIndex != targetIdx)
                {
                    continue;
                }

                for (var b = 0; b < 16; b++)
                {
                    rowAddr[b] = row.DestAddrV6[b];
                }

                foreach (var (targetAddr, targetLen) in parsedTargets)
                {
                    if (row.DestPrefixLength == targetLen && rowAddr.SequenceEqual(targetAddr))
                    {
                        _ = DeleteIpForwardEntry2(ref row);
                        break;
                    }
                }
            }
        }
        finally
        {
            FreeMibTable(tablePtr);
        }
    }

    //назначение статического ipv4 или ipv6 адреса на адаптер с очисткой старых адресов этого семейства
    public static unsafe uint SetUnicastIpAddress(ulong interfaceLuid, string ipStr, byte prefixLength)
    {
        if (interfaceLuid == 0 || !IPAddress.TryParse(ipStr, out var ip))
        {
            return 87;
        }

        var family = ip.AddressFamily == AddressFamily.InterNetworkV6 ? AfInet6 : AfInet;
        var desiredBytes = ip.GetAddressBytes();
        var alreadyConfigured = false;

        if (GetUnicastIpAddressTable(family, out var tablePtr) == 0 && tablePtr != IntPtr.Zero)
        {
            try
            {
                var count = *(uint*)tablePtr;
                var rows = (MibUnicastIpAddressRow*)((byte*)tablePtr + 8);
                Span<byte> existingBytes = stackalloc byte[family == AfInet6 ? 16 : 4];
                for (var i = 0; i < count; i++)
                {
                    var existing = rows[i];
                    if (existing.InterfaceLuid != interfaceLuid || existing.Family != family)
                    {
                        continue;
                    }

                    if (family == AfInet)
                    {
                        for (var b = 0; b < 4; b++)
                        {
                            existingBytes[b] = existing.AddrV4[b];
                        }
                    }
                    else
                    {
                        for (var b = 0; b < 16; b++)
                        {
                            existingBytes[b] = existing.AddrV6[b];
                        }
                    }

                    if (existingBytes.SequenceEqual(desiredBytes) && existing.OnLinkPrefixLength == prefixLength)
                    {
                        alreadyConfigured = true;
                    }
                    else
                    {
                        _ = DeleteUnicastIpAddressEntry(ref existing);
                    }
                }
            }
            finally
            {
                FreeMibTable(tablePtr);
            }
        }

        if (alreadyConfigured)
        {
            return 0;
        }

        InitializeUnicastIpAddressEntry(out var row);
        row.InterfaceLuid = interfaceLuid;
        row.Family = family;
        row.OnLinkPrefixLength = prefixLength;
        row.PrefixOrigin = 1; // IpPrefixOriginManual
        row.SuffixOrigin = 1; // IpSuffixOriginManual
        row.ValidLifetime = uint.MaxValue;
        row.PreferredLifetime = uint.MaxValue;
        row.SkipAsSource = 0;

        if (family == AfInet)
        {
            for (var b = 0; b < 4; b++)
            {
                row.AddrV4[b] = desiredBytes[b];
            }
        }
        else
        {
            for (var b = 0; b < 16; b++)
            {
                row.AddrV6[b] = desiredBytes[b];
            }
        }

        var status = CreateUnicastIpAddressEntry(ref row);
        return status == ErrorObjectAlreadyExists ? 0u : status;
    }

    //настройка mtu и метрики интерфейса напрямую через iphlpapi
    public static uint ConfigureIpInterface(ulong interfaceLuid, ushort family, uint? mtu, uint? metric)
    {
        if (interfaceLuid == 0)
        {
            return 87;
        }

        InitializeIpInterfaceEntry(out var row);
        row.Family = family;
        row.InterfaceLuid = interfaceLuid;

        if (GetIpInterfaceEntry(ref row) != 0)
        {
            InitializeIpInterfaceEntry(out row);
            row.Family = family;
            row.InterfaceLuid = interfaceLuid;
        }

        if (family == AfInet)
        {
            row.SitePrefixLength = 0;
        }

        if (mtu is { } targetMtu)
        {
            row.NlMtu = targetMtu;
        }

        if (metric is { } targetMetric)
        {
            row.UseAutomaticMetric = 0;
            row.Metric = targetMetric;
        }

        return SetIpInterfaceEntry(ref row);
    }

    //чтение текущих dns-настроек интерфейса (null означает получение по dhcp)
    public static string? GetInterfaceDnsServers(Guid interfaceGuid, bool ipv6)
    {
        if (interfaceGuid == Guid.Empty)
        {
            return null;
        }

        var settings = new DnsInterfaceSettings
        {
            Version = 1,
            Flags = ipv6 ? DnsSettingIpv6 | DnsSettingNameServer : DnsSettingNameServer
        };

        try
        {
            if (GetInterfaceDnsSettings(interfaceGuid, ref settings) != 0)
            {
                return null;
            }

            var value = Marshal.PtrToStringUni(settings.NameServer);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        finally
        {
            FreeInterfaceDnsSettings(ref settings);
        }
    }

    //установка статических dns серверов или сброс в dhcp (при null/пустой строке)
    public static uint SetInterfaceDnsServers(Guid interfaceGuid, bool ipv6, string? serversCsv)
    {
        if (interfaceGuid == Guid.Empty)
        {
            return 87;
        }

        var nsPtr = string.IsNullOrWhiteSpace(serversCsv)
            ? IntPtr.Zero
            : Marshal.StringToHGlobalUni(serversCsv);
        try
        {
            var settings = new DnsInterfaceSettings
            {
                Version = 1,
                Flags = ipv6 ? DnsSettingIpv6 | DnsSettingNameServer : DnsSettingNameServer,
                NameServer = nsPtr
            };

            return SetInterfaceDnsSettings(interfaceGuid, in settings);
        }
        finally
        {
            if (nsPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(nsPtr);
            }
        }
    }

    //поиск удаленных ipv4 адресов установленных tcp соединений по целевым портам
    public static unsafe List<string> GetEstablishedTcpRemoteIps(params ushort[] targetPorts)
    {
        var result = new List<string>();
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, 5, 0);
        if (size <= 0)
        {
            return result;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, 5, 0) != 0)
            {
                return result;
            }

            var count = *(int*)buffer;
            var rows = (MibTcpRowOwnerPid*)((byte*)buffer + sizeof(int));
            Span<byte> ipBytes = stackalloc byte[4];
            for (var i = 0; i < count; i++)
            {
                //5 = MIB_TCP_STATE_ESTAB
                if (rows[i].State != 5)
                {
                    continue;
                }

                var rawPort = (ushort)rows[i].RemotePort;
                var port = (ushort)((rawPort >> 8) | (rawPort << 8));
                var matched = false;
                foreach (var p in targetPorts)
                {
                    if (port == p)
                    {
                        matched = true;
                        break;
                    }
                }

                if (!matched)
                {
                    continue;
                }

                var remoteAddr = rows[i].RemoteAddr;
                MemoryMarshal.Write(ipBytes, in remoteAddr);
                var ip = new IPAddress(ipBytes);
                if (!IPAddress.IsLoopback(ip))
                {
                    result.Add(ip.ToString());
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MibIpForwardRow
    {
        public uint DwForwardDest;
        public uint DwForwardMask;
        public uint DwForwardPolicy;
        public uint DwForwardNextHop;
        public uint DwForwardIfIndex;
        public uint DwForwardType;
        public uint DwForwardProto;
        public uint DwForwardAge;
        public uint DwForwardNextHopAS;
        public uint DwForwardMetric1;
        public uint DwForwardMetric2;
        public uint DwForwardMetric3;
        public uint DwForwardMetric4;
        public uint DwForwardMetric5;
    }

    [StructLayout(LayoutKind.Explicit, Size = 104)]
    private struct MibIpForwardRow2
    {
        [FieldOffset(0)] public ulong InterfaceLuid;
        [FieldOffset(8)] public uint InterfaceIndex;
        [FieldOffset(12)] public ushort DestFamily;
        [FieldOffset(16)] public uint DestAddrV4;
        [FieldOffset(20)] public fixed byte DestAddrV6[16];
        [FieldOffset(40)] public byte DestPrefixLength;
        [FieldOffset(44)] public ushort NextHopFamily;
        [FieldOffset(48)] public uint NextHopAddrV4;
        [FieldOffset(52)] public fixed byte NextHopAddrV6[16];
        [FieldOffset(84)] public uint Metric;
        [FieldOffset(88)] public int Protocol;
    }

    [StructLayout(LayoutKind.Explicit, Size = 80)]
    private struct MibUnicastIpAddressRow
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(4)] public fixed byte AddrV4[4];
        [FieldOffset(8)] public fixed byte AddrV6[16];
        [FieldOffset(32)] public ulong InterfaceLuid;
        [FieldOffset(40)] public uint InterfaceIndex;
        [FieldOffset(44)] public int PrefixOrigin;
        [FieldOffset(48)] public int SuffixOrigin;
        [FieldOffset(52)] public uint ValidLifetime;
        [FieldOffset(56)] public uint PreferredLifetime;
        [FieldOffset(60)] public byte OnLinkPrefixLength;
        [FieldOffset(61)] public byte SkipAsSource;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpInterfaceRow
    {
        public ushort Family;
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public uint MaxReassemblySize;
        public ulong InterfaceIdentifier;
        public uint MinRouterAdvertisementInterval;
        public uint MaxRouterAdvertisementInterval;
        public byte AdvertisingEnabled;
        public byte ForwardingEnabled;
        public byte WeakHostSend;
        public byte WeakHostReceive;
        public byte UseAutomaticMetric;
        public byte UseNeighborUnreachabilityDetection;
        public byte ManagedAddressConfigurationSupported;
        public byte OtherStatefulConfigurationSupported;
        public byte AdvertiseDefaultRoute;
        public int RouterDiscoveryBehavior;
        public uint DadTransmits;
        public uint BaseReachableTime;
        public uint RetransmitTime;
        public uint PathMtuDiscoveryTimeout;
        public int LinkLocalAddressBehavior;
        public uint LinkLocalAddressTimeout;
        public fixed uint ZoneIndices[16];
        public uint SitePrefixLength;
        public uint Metric;
        public uint NlMtu;
        public byte Connected;
        public byte SupportsWakeUpPatterns;
        public byte SupportsNeighborDiscovery;
        public byte SupportsRouterDiscovery;
        public uint ReachableTime;
        public byte TransmitOffload;
        public byte ReceiveOffload;
        public byte DisableDefaultRoutes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DnsInterfaceSettings
    {
        public uint Version;
        public ulong Flags;
        public IntPtr Domain;
        public IntPtr NameServer;
        public IntPtr SearchList;
        public uint RegistrationEnabled;
        public uint RegisterAdapterName;
        public uint EnableLlmnr;
        public uint QueryAdapterName;
        public IntPtr ProfileNameServer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public int OwningPid;
    }
}
