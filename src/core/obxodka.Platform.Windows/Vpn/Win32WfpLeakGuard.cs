using System.Buffers.Binary;

namespace obxodka.Platforms.Windows;

//нативный wfp anti-leak / kill-switch фильтр (fwpuclnt.dll) для блокировки утечек ipv4/ipv6 и dns мимо туннеля
[SupportedOSPlatform("windows10.0.19041.0")]
internal static partial class Win32WfpLeakGuard
{
    private const uint FwpmSessionFlagDynamic = 0x00000001;
    private const uint RpcCAuthnDefault = 0xFFFFFFFF;
    private const uint ErrorSuccess = 0;

    private const uint FwpActionBlock = 0x00000001 | 0x00001000;
    private const uint FwpActionPermit = 0x00000002 | 0x00001000;

    private const uint FwpUint8 = 1;
    private const uint FwpUint16 = 2;
    private const uint FwpUint32 = 3;
    private const uint FwpUint64 = 4;
    private const uint FwpByteBlobType = 12;
    private const uint FwpV4AddrMask = 0x100;
    private const uint FwpV6AddrMask = 0x101;

    private const uint FwpMatchEqual = 0;
    private const uint FwpMatchFlagsAllSet = 6;
    private const uint FwpConditionFlagIsLoopback = 0x00000001;

    //фиксированный ключ динамического sublayer с максимальным приоритетом
    private static readonly Guid s_subLayerKey = new(0x7a4e1c2b, 0x91d3, 0x4b8e, 0xa2, 0x41, 0x0b, 0x77, 0xc3, 0x52, 0x19, 0x01);

    //guid слоев и условий фильтрации из windows sdk (fwpmu.h)
    private static readonly Guid s_layerAleAuthConnectV4 = new(0xc38d57d1, 0x05a7, 0x4c33, 0x90, 0x4f, 0x7f, 0xbc, 0xee, 0xe6, 0x0e, 0x82);
    private static readonly Guid s_layerAleAuthConnectV6 = new(0x4a72393b, 0x319f, 0x44bc, 0x84, 0xc3, 0xba, 0x54, 0xdc, 0xb3, 0xb6, 0xb4);
    private static readonly Guid s_condIpLocalInterface = new(0x4cd62a49, 0x59c3, 0x4969, 0xb7, 0xf3, 0xbd, 0xa5, 0xd3, 0x28, 0x90, 0xa4);
    private static readonly Guid s_condIpRemoteAddress = new(0xb235ae9a, 0x1d64, 0x49b8, 0xa4, 0x4c, 0x5f, 0xf3, 0xd9, 0x09, 0x50, 0x45);
    private static readonly Guid s_condIpRemotePort = new(0xc35a604d, 0xd22b, 0x4e1a, 0x91, 0xb4, 0x68, 0xf6, 0x74, 0xee, 0x67, 0x4b);
    private static readonly Guid s_condFlags = new(0x632ce23b, 0x5167, 0x435c, 0x86, 0xd7, 0xe9, 0x03, 0x68, 0x4a, 0xa8, 0x0c);
    private static readonly Guid s_condAleAppId = new(0xd78e1e87, 0x8644, 0x4ea5, 0x94, 0x37, 0xd8, 0x09, 0xec, 0xef, 0xc9, 0x71);

    private static readonly Lock s_sync = new();
    private static readonly List<ulong> s_activeFilterIds = [];
    private static readonly HashSet<(uint NetHost, uint MaskHost)> s_allowedDynamicV4 = [];

    private static IntPtr s_engineHandle;
    private static ulong s_tunLuid;
    private static string[] s_serverIps = [];
    private static bool s_splitEnabled;
    private static string[] s_splitExePaths = [];
    private static string[] s_splitExeNames = [];
    private static (uint NetNetOrder, uint MaskNetOrder)[] s_splitPinnedRoutes = [];

    public static bool IsActive
    {
        get
        {
            lock (s_sync)
            {
                return s_engineHandle != IntPtr.Zero;
            }
        }
    }

    //включение или обновление фильтров защиты от утечек трафика и dns
    public static void Enable(ulong tunLuid, IReadOnlyList<string> serverIps)
    {
        lock (s_sync)
        {
            s_tunLuid = tunLuid;
            s_serverIps = [.. serverIps.Where(ip => !string.IsNullOrWhiteSpace(ip))];

            if (!EnsureEngineOpenUnsafe())
            {
                return;
            }

            RebuildFiltersUnsafe();
        }
    }

    //обновление снимка правил сплит-туннеля (вызывается движком без обратной зависимости)
    public static void SetSplitTunnelSnapshot(
        bool enabled,
        string[] exePaths,
        string[] exeNames,
        IReadOnlyList<(uint NetNetOrder, uint MaskNetOrder)> pinnedRoutes)
    {
        lock (s_sync)
        {
            s_splitEnabled = enabled;
            s_splitExePaths = exePaths;
            s_splitExeNames = exeNames;
            s_splitPinnedRoutes = [.. pinnedRoutes];

            if (s_engineHandle == IntPtr.Zero)
            {
                return;
            }

            RebuildFiltersUnsafe();
        }
    }

    //инкрементальное добавление разрешения в обход для нового маршрута сплит-туннеля
    public static void AllowBypassIpv4Route(uint netNetOrder, uint maskNetOrder)
    {
        var netHost = BinaryPrimitives.ReverseEndianness(netNetOrder);
        var maskHost = BinaryPrimitives.ReverseEndianness(maskNetOrder);

        lock (s_sync)
        {
            if (s_engineHandle == IntPtr.Zero || !s_allowedDynamicV4.Add((netHost, maskHost)))
            {
                return;
            }

            if (FwpmTransactionBegin0(s_engineHandle, 0) != ErrorSuccess)
            {
                return;
            }

            var committed = false;
            try
            {
                if (TryAddPermitIpv4RemoteSubnetUnsafe(netHost, maskHost, 11, "Obxodka Allow SplitV4", out var filterId))
                {
                    s_activeFilterIds.Add(filterId);
                }

                if (FwpmTransactionCommit0(s_engineHandle) == ErrorSuccess)
                {
                    committed = true;
                }
            }
            finally
            {
                if (!committed)
                {
                    _ = FwpmTransactionAbort0(s_engineHandle);
                }
            }
        }
    }

    //отключение wfp-защиты (при закрытии динамической сессии ядро само удаляет все фильтры)
    public static void Disable()
    {
        lock (s_sync)
        {
            s_activeFilterIds.Clear();
            s_allowedDynamicV4.Clear();
            s_tunLuid = 0;
            s_serverIps = [];

            if (s_engineHandle == IntPtr.Zero)
            {
                return;
            }

            var status = FwpmEngineClose0(s_engineHandle);
            s_engineHandle = IntPtr.Zero;
            AppLogger.Log($"[WFP] Anti-Leak filter disabled (status={status}).");
        }
    }

    //открытие динамической сессии wfp и регистрация выделенного sublayer
    private static bool EnsureEngineOpenUnsafe()
    {
        if (s_engineHandle != IntPtr.Zero)
        {
            return true;
        }

        var nameHandle = GCHandle.Alloc("ObxodkaWfpLeakGuard", GCHandleType.Pinned);
        var descHandle = GCHandle.Alloc("Obxodka dynamic anti-leak firewall", GCHandleType.Pinned);
        try
        {
            var namePtr = nameHandle.AddrOfPinnedObject();
            var descPtr = descHandle.AddrOfPinnedObject();
            var session = new FwpmSession0
            {
                DisplayData = new FwpmDisplayData0 { Name = namePtr, Description = descPtr },
                Flags = FwpmSessionFlagDynamic
            };

            var openStatus = FwpmEngineOpen0(null, RpcCAuthnDefault, IntPtr.Zero, in session, out var engine);
            if (openStatus != ErrorSuccess || engine == IntPtr.Zero)
            {
                AppLogger.LogWarning($"[WFP WARN] FwpmEngineOpen0 failed: code=0x{openStatus:X}");
                return false;
            }

            if (FwpmTransactionBegin0(engine, 0) != ErrorSuccess)
            {
                _ = FwpmEngineClose0(engine);
                return false;
            }

            var subLayer = new FwpmSubLayer0
            {
                SubLayerKey = s_subLayerKey,
                DisplayData = new FwpmDisplayData0 { Name = namePtr, Description = descPtr },
                Weight = 0xFFFF
            };

            var subStatus = FwpmSubLayerAdd0(engine, in subLayer, IntPtr.Zero);
            if (subStatus != ErrorSuccess)
            {
                _ = FwpmTransactionAbort0(engine);
                _ = FwpmEngineClose0(engine);
                AppLogger.LogWarning($"[WFP WARN] FwpmSubLayerAdd0 failed: code=0x{subStatus:X}");
                return false;
            }

            var commitStatus = FwpmTransactionCommit0(engine);
            if (commitStatus != ErrorSuccess)
            {
                _ = FwpmTransactionAbort0(engine);
                _ = FwpmEngineClose0(engine);
                AppLogger.LogWarning($"[WFP WARN] SubLayer commit failed: code=0x{commitStatus:X}");
                return false;
            }

            s_engineHandle = engine;
            return true;
        }
        finally
        {
            nameHandle.Free();
            descHandle.Free();
        }
    }

    //атомарная пересборка полного набора правил в одной транзакции без окна утечки
    private static void RebuildFiltersUnsafe()
    {
        if (s_engineHandle == IntPtr.Zero)
        {
            return;
        }

        if (FwpmTransactionBegin0(s_engineHandle, 0) != ErrorSuccess)
        {
            return;
        }

        var committed = false;
        try
        {
            foreach (var oldId in s_activeFilterIds)
            {
                _ = FwpmFilterDeleteById0(s_engineHandle, oldId);
            }

            s_allowedDynamicV4.Clear();

            var newIds = new List<ulong>(64);

            //приоритет 15: разрешить трафик внутри туннеля wintun (v4 и v6)
            if (s_tunLuid != 0)
            {
                AddPermitInterfaceRulesUnsafe(s_tunLuid, newIds);
            }

            //приоритет 15: разрешить loopback (127.0.0.0/8 и ::1)
            AddPermitLoopbackRulesUnsafe(newIds);

            //приоритет 15: разрешить сам процесс обходки (транспортные сокеты и резолв)
            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            {
                AddPermitAppRulesUnsafe(Environment.ProcessPath, "Obxodka Allow Self", newIds);
            }

            //приоритет 14: жестко блокировать прямой dns (порт 53) мимо туннеля и loopback
            AddBlockDnsPortRulesUnsafe(newIds);

            //приоритет 13: разрешить ip vpn-серверов на физическом адаптере
            foreach (var serverIp in s_serverIps)
            {
                if (!IPAddress.TryParse(serverIp, out var parsed))
                {
                    continue;
                }

                if (parsed.AddressFamily == AddressFamily.InterNetwork)
                {
                    var hostIp = ReadIpv4HostOrder(parsed);
                    if (TryAddPermitIpv4RemoteSubnetUnsafe(hostIp, 0xFFFFFFFFu, 13, "Obxodka Allow ServerV4", out var id))
                    {
                        newIds.Add(id);
                    }
                }
                else if (parsed.AddressFamily == AddressFamily.InterNetworkV6 &&
                         TryAddPermitIpv6RemoteSubnetUnsafe(parsed, 128, "Obxodka Allow ServerV6", out var id6))
                {
                    newIds.Add(id6);
                }
            }

            //приоритет 13: разрешить локальную сеть (lan / link-local / dhcp / multicast link)
            AddPermitLanRulesUnsafe(newIds);

            //приоритет 12: блокировать прямой webrtc stun/turn (3478, 5349, 19302) мимо туннеля поверх ру-суперсетей
            AddBlockDirectStunPortRulesUnsafe(newIds);

            //приоритет 13 (приложения) / 11 (подсети): разрешить правила активного split tunnel только при активном туннеле
            if (s_splitEnabled && s_tunLuid != 0)
            {
                AddSplitTunnelPermitRulesUnsafe(newIds);
            }

            //приоритет 1: блокировать весь остальной исходящий ipv4 и ipv6 трафик в обход туннеля
            if (TryAddSimpleFilterUnsafe(s_layerAleAuthConnectV4, FwpActionBlock, 1, "Obxodka Block LeakV4", IntPtr.Zero, 0, out var blockV4))
            {
                newIds.Add(blockV4);
            }

            if (TryAddSimpleFilterUnsafe(s_layerAleAuthConnectV6, FwpActionBlock, 1, "Obxodka Block LeakV6", IntPtr.Zero, 0, out var blockV6))
            {
                newIds.Add(blockV6);
            }

            var commitStatus = FwpmTransactionCommit0(s_engineHandle);
            if (commitStatus == ErrorSuccess)
            {
                committed = true;
                s_activeFilterIds.Clear();
                s_activeFilterIds.AddRange(newIds);
                AppLogger.Log($"[WFP] Anti-Leak rules active: {s_activeFilterIds.Count} filters (tunLuid={s_tunLuid}).");
            }
            else
            {
                AppLogger.LogWarning($"[WFP WARN] FwpmTransactionCommit0 failed: code=0x{commitStatus:X}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[WFP WARN] RebuildFiltersUnsafe error: {ex.Message}");
        }
        finally
        {
            if (!committed)
            {
                _ = FwpmTransactionAbort0(s_engineHandle);
            }
        }
    }

    //разрешение трафика через интерфейс туннеля wintun по его luid
    private static void AddPermitInterfaceRulesUnsafe(ulong luid, List<ulong> outIds)
    {
        unsafe
        {
            var luidCopy = luid;
            var cond = new FwpmFilterCondition0
            {
                FieldKey = s_condIpLocalInterface,
                MatchType = FwpMatchEqual,
                ConditionValue = new FwpConditionValue0 { Type = FwpUint64, Ptr = (IntPtr)(&luidCopy) }
            };

            if (TryAddSimpleFilterUnsafe(s_layerAleAuthConnectV4, FwpActionPermit, 15, "Obxodka Allow TunV4", (IntPtr)(&cond), 1, out var id4))
            {
                outIds.Add(id4);
            }

            if (TryAddSimpleFilterUnsafe(s_layerAleAuthConnectV6, FwpActionPermit, 15, "Obxodka Allow TunV6", (IntPtr)(&cond), 1, out var id6))
            {
                outIds.Add(id6);
            }
        }
    }

    //разрешение loopback-соединений
    private static void AddPermitLoopbackRulesUnsafe(List<ulong> outIds)
    {
        unsafe
        {
            var cond = new FwpmFilterCondition0
            {
                FieldKey = s_condFlags,
                MatchType = FwpMatchFlagsAllSet,
                ConditionValue = new FwpConditionValue0 { Type = FwpUint32, Uint32 = FwpConditionFlagIsLoopback }
            };

            if (TryAddSimpleFilterUnsafe(s_layerAleAuthConnectV4, FwpActionPermit, 15, "Obxodka Allow LoopbackV4", (IntPtr)(&cond), 1, out var id4))
            {
                outIds.Add(id4);
            }

            if (TryAddSimpleFilterUnsafe(s_layerAleAuthConnectV6, FwpActionPermit, 15, "Obxodka Allow LoopbackV6", (IntPtr)(&cond), 1, out var id6))
            {
                outIds.Add(id6);
            }
        }
    }

    //разрешение трафика конкретного исполняемого файла по пути через ale app id
    private static void AddPermitAppRulesUnsafe(string exePath, string filterName, List<ulong> outIds, byte weight = 15)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return;
        }

        if (FwpmGetAppIdFromFileName0(exePath, out var appBlobPtr) != ErrorSuccess || appBlobPtr == IntPtr.Zero)
        {
            return;
        }

        try
        {
            unsafe
            {
                var cond = new FwpmFilterCondition0
                {
                    FieldKey = s_condAleAppId,
                    MatchType = FwpMatchEqual,
                    ConditionValue = new FwpConditionValue0 { Type = FwpByteBlobType, Ptr = appBlobPtr }
                };

                if (TryAddSimpleFilterUnsafe(s_layerAleAuthConnectV4, FwpActionPermit, weight, filterName, (IntPtr)(&cond), 1, out var id4))
                {
                    outIds.Add(id4);
                }

                if (TryAddSimpleFilterUnsafe(s_layerAleAuthConnectV6, FwpActionPermit, weight, filterName, (IntPtr)(&cond), 1, out var id6))
                {
                    outIds.Add(id6);
                }
            }
        }
        finally
        {
            FwpmFreeMemory0(ref appBlobPtr);
        }
    }

    //блокировка открытых dns-запросов (порт 53) в обход туннеля и loopback
    private static void AddBlockDnsPortRulesUnsafe(List<ulong> outIds)
    {
        unsafe
        {
            var cond = new FwpmFilterCondition0
            {
                FieldKey = s_condIpRemotePort,
                MatchType = FwpMatchEqual,
                ConditionValue = new FwpConditionValue0 { Type = FwpUint16, Uint16 = 53 }
            };

            if (TryAddSimpleFilterUnsafe(s_layerAleAuthConnectV4, FwpActionBlock, 14, "Obxodka Block DnsV4", (IntPtr)(&cond), 1, out var id4))
            {
                outIds.Add(id4);
            }

            if (TryAddSimpleFilterUnsafe(s_layerAleAuthConnectV6, FwpActionBlock, 14, "Obxodka Block DnsV6", (IntPtr)(&cond), 1, out var id6))
            {
                outIds.Add(id6);
            }
        }
    }

    //блокировка прямого webrtc stun/turn трафика (3478, 5349, 19302) в обход туннеля для защиты от утечки ip через ру-суперсети
    private static void AddBlockDirectStunPortRulesUnsafe(List<ulong> outIds)
    {
        unsafe
        {
            foreach (var port in StunPacketInspector.StandardStunPorts)
            {
                var cond = new FwpmFilterCondition0
                {
                    FieldKey = s_condIpRemotePort,
                    MatchType = FwpMatchEqual,
                    ConditionValue = new FwpConditionValue0 { Type = FwpUint16, Uint16 = port }
                };

                if (TryAddSimpleFilterUnsafe(s_layerAleAuthConnectV4, FwpActionBlock, 12, $"Obxodka Block StunV4 {port}", (IntPtr)(&cond), 1, out var id4))
                {
                    outIds.Add(id4);
                }

                if (TryAddSimpleFilterUnsafe(s_layerAleAuthConnectV6, FwpActionBlock, 12, $"Obxodka Block StunV6 {port}", (IntPtr)(&cond), 1, out var id6))
                {
                    outIds.Add(id6);
                }
            }
        }
    }

    //разрешение локальных приватных сетей и широковещательного dhcp
    private static void AddPermitLanRulesUnsafe(List<ulong> outIds)
    {
        ReadOnlySpan<(uint Net, uint Mask)> v4Lan =
        [
            (0x0A000000u, 0xFF000000u), //10.0.0.0/8
            (0xAC100000u, 0xFFF00000u), //172.16.0.0/12
            (0xC0A80000u, 0xFFFF0000u), //192.168.0.0/16
            (0xA9FE0000u, 0xFFFF0000u), //169.254.0.0/16
            (0xFFFFFFFFu, 0xFFFFFFFFu)  //255.255.255.255/32 (dhcp)
        ];

        foreach (var (net, mask) in v4Lan)
        {
            if (TryAddPermitIpv4RemoteSubnetUnsafe(net, mask, 13, "Obxodka Allow LanV4", out var id4))
            {
                outIds.Add(id4);
            }
        }

        ReadOnlySpan<(string Prefix, byte Len)> v6Lan =
        [
            ("fe80::", 10),
            ("fc00::", 7),
            ("ff02::", 16)
        ];

        foreach (var (prefix, len) in v6Lan)
        {
            if (IPAddress.TryParse(prefix, out var addr6) &&
                TryAddPermitIpv6RemoteSubnetUnsafe(addr6, len, "Obxodka Allow LanV6", out var id6))
            {
                outIds.Add(id6);
            }
        }
    }

    //синхронизация разрешений wfp со списком приложений и маршрутов раздельного туннеля
    private static void AddSplitTunnelPermitRulesUnsafe(List<ulong> outIds)
    {
        var allowedPaths = new HashSet<string>(s_splitExePaths, StringComparer.OrdinalIgnoreCase);

        //для правил по имени файла ищем полный путь среди запущенных процессов
        if (s_splitExeNames.Length > 0)
        {
            var nameSet = new HashSet<string>(s_splitExeNames, StringComparer.OrdinalIgnoreCase);
            foreach (var proc in Process.GetProcesses())
            {
                using (proc)
                {
                    try
                    {
                        if (!nameSet.Contains(proc.ProcessName) && !nameSet.Contains($"{proc.ProcessName}.exe"))
                        {
                            continue;
                        }

                        var mainPath = proc.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(mainPath))
                        {
                            _ = allowedPaths.Add(mainPath);
                        }
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        //игнорируем системные процессы без доступа к MainModule
                    }
                }
            }
        }

        foreach (var path in allowedPaths)
        {
            AddPermitAppRulesUnsafe(path, "Obxodka Allow SplitApp", outIds, 13);
        }

        foreach (var (netNetOrder, maskNetOrder) in s_splitPinnedRoutes)
        {
            var netHost = BinaryPrimitives.ReverseEndianness(netNetOrder);
            var maskHost = BinaryPrimitives.ReverseEndianness(maskNetOrder);
            if (!s_allowedDynamicV4.Add((netHost, maskHost)))
            {
                continue;
            }

            if (TryAddPermitIpv4RemoteSubnetUnsafe(netHost, maskHost, 11, "Obxodka Allow SplitV4", out var id4))
            {
                outIds.Add(id4);
            }
        }
    }

    //добавление разрешающего фильтра для ipv4-подсети (addr и mask строго в host byte order)
    private static bool TryAddPermitIpv4RemoteSubnetUnsafe(
        uint netHostOrder,
        uint maskHostOrder,
        byte weight,
        string name,
        out ulong filterId)
    {
        unsafe
        {
            var addrMask = new FwpV4AddrAndMask { Addr = netHostOrder, Mask = maskHostOrder };
            var cond = new FwpmFilterCondition0
            {
                FieldKey = s_condIpRemoteAddress,
                MatchType = FwpMatchEqual,
                ConditionValue = new FwpConditionValue0 { Type = FwpV4AddrMask, Ptr = (IntPtr)(&addrMask) }
            };

            return TryAddSimpleFilterUnsafe(s_layerAleAuthConnectV4, FwpActionPermit, weight, name, (IntPtr)(&cond), 1, out filterId);
        }
    }

    //добавление разрешающего фильтра для ipv6-префикса
    private static bool TryAddPermitIpv6RemoteSubnetUnsafe(
        IPAddress addr6,
        byte prefixLength,
        string name,
        out ulong filterId)
    {
        unsafe
        {
            var v6Mask = new FwpV6AddrAndMask { PrefixLength = prefixLength };
            Span<byte> addrBytes = stackalloc byte[16];
            _ = addr6.TryWriteBytes(addrBytes, out _);
            for (var i = 0; i < 16; i++)
            {
                v6Mask.Addr[i] = addrBytes[i];
            }

            var cond = new FwpmFilterCondition0
            {
                FieldKey = s_condIpRemoteAddress,
                MatchType = FwpMatchEqual,
                ConditionValue = new FwpConditionValue0 { Type = FwpV6AddrMask, Ptr = (IntPtr)(&v6Mask) }
            };

            return TryAddSimpleFilterUnsafe(s_layerAleAuthConnectV6, FwpActionPermit, 13, name, (IntPtr)(&cond), 1, out filterId);
        }
    }

    //добавление фильтра в активную транзакцию wfp
    private static bool TryAddSimpleFilterUnsafe(
        Guid layerKey,
        uint actionType,
        byte weight,
        string filterName,
        IntPtr conditions,
        uint conditionCount,
        out ulong filterId)
    {
        var nameHandle = GCHandle.Alloc(filterName, GCHandleType.Pinned);
        try
        {
            var filter = new FwpmFilter0
            {
                DisplayData = new FwpmDisplayData0 { Name = nameHandle.AddrOfPinnedObject() },
                LayerKey = layerKey,
                SubLayerKey = s_subLayerKey,
                Weight = new FwpValue0 { Type = FwpUint8, Uint8 = weight },
                NumFilterConditions = conditionCount,
                FilterCondition = conditions,
                Action = new FwpmAction0 { Type = actionType }
            };

            var status = FwpmFilterAdd0(s_engineHandle, in filter, IntPtr.Zero, out filterId);
            if (status == ErrorSuccess)
            {
                return true;
            }

            AppLogger.LogWarning($"[WFP WARN] FwpmFilterAdd0('{filterName}') failed: code=0x{status:X}");
            return false;
        }
        finally
        {
            nameHandle.Free();
        }
    }

    //чтение ipv4 адреса в host byte order для FWP_V4_ADDR_AND_MASK
    private static uint ReadIpv4HostOrder(IPAddress ip)
    {
        Span<byte> bytes = stackalloc byte[4];
        _ = ip.TryWriteBytes(bytes, out _);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    [LibraryImport("fwpuclnt.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint FwpmEngineOpen0(
        [MarshalAs(UnmanagedType.LPWStr)] string? serverName,
        uint authnService,
        IntPtr authIdentity,
        in FwpmSession0 session,
        out IntPtr engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint FwpmEngineClose0(IntPtr engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint FwpmTransactionBegin0(IntPtr engineHandle, uint flags);

    [LibraryImport("fwpuclnt.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint FwpmTransactionCommit0(IntPtr engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint FwpmTransactionAbort0(IntPtr engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint FwpmSubLayerAdd0(IntPtr engineHandle, in FwpmSubLayer0 subLayer, IntPtr sd);

    [LibraryImport("fwpuclnt.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint FwpmFilterAdd0(IntPtr engineHandle, in FwpmFilter0 filter, IntPtr sd, out ulong id);

    [LibraryImport("fwpuclnt.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint FwpmFilterDeleteById0(IntPtr engineHandle, ulong id);

    [LibraryImport("fwpuclnt.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint FwpmGetAppIdFromFileName0(
        [MarshalAs(UnmanagedType.LPWStr)] string fileName,
        out IntPtr appId);

    [LibraryImport("fwpuclnt.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void FwpmFreeMemory0(ref IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmDisplayData0
    {
        public IntPtr Name;
        public IntPtr Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpByteBlob
    {
        public uint Size;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmSession0
    {
        public Guid SessionKey;
        public FwpmDisplayData0 DisplayData;
        public uint Flags;
        public uint TxnWaitTimeoutInMSec;
        public uint ProcessId;
        public IntPtr Sid;
        public IntPtr Username;
        public int KernelMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmSubLayer0
    {
        public Guid SubLayerKey;
        public FwpmDisplayData0 DisplayData;
        public uint Flags;
        public IntPtr ProviderKey;
        public FwpByteBlob ProviderData;
        public ushort Weight;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct FwpValue0
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public byte Uint8;
        [FieldOffset(8)] public IntPtr Ptr;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct FwpConditionValue0
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Uint16;
        [FieldOffset(8)] public uint Uint32;
        [FieldOffset(8)] public IntPtr Ptr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmFilterCondition0
    {
        public Guid FieldKey;
        public uint MatchType;
        public FwpConditionValue0 ConditionValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmAction0
    {
        public uint Type;
        public Guid FilterType;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct FwpmContextUnion
    {
        [FieldOffset(0)] public ulong RawContext;
        [FieldOffset(0)] public Guid ProviderContextKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmFilter0
    {
        public Guid FilterKey;
        public FwpmDisplayData0 DisplayData;
        public uint Flags;
        public IntPtr ProviderKey;
        public FwpByteBlob ProviderData;
        public Guid LayerKey;
        public Guid SubLayerKey;
        public FwpValue0 Weight;
        public uint NumFilterConditions;
        public IntPtr FilterCondition;
        public FwpmAction0 Action;
        public FwpmContextUnion Context;
        public IntPtr Reserved;
        public ulong FilterId;
        public FwpValue0 EffectiveWeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpV4AddrAndMask
    {
        public uint Addr;
        public uint Mask;
    }

    private struct FwpV6AddrAndMask
    {
        public fixed byte Addr[16];
        // ReSharper disable once NotAccessedField.Local
        public byte PrefixLength;
    }
}
