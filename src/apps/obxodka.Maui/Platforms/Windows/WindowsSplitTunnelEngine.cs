using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using obxodka.Core.Models;

namespace obxodka.Platforms.Windows;

[SupportedOSPlatform("windows10.0.19041.0")]
internal static partial class WindowsSplitTunnelEngine
{
    public static SplitTunnelPolicy Policy { get; } = new();

    public static event Action? OnRoutesUpdated;

    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_IPFORWARDROW
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

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW_OWNER_PID
    {
        public ulong LocalAddrLo;
        public ulong LocalAddrHi;
        public uint LocalScopeId;
        public uint LocalPort;
        public ulong RemoteAddrLo;
        public ulong RemoteAddrHi;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDP6ROW_OWNER_PID
    {
        public ulong LocalAddrLo;
        public ulong LocalAddrHi;
        public uint LocalScopeId;
        public uint LocalPort;
        public uint OwningPid;
    }

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    private static partial int GetBestRoute(uint dwDestAddr, uint dwSourceAddr, out MIB_IPFORWARDROW pBestRoute);

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    private static partial int CreateIpForwardEntry(ref MIB_IPFORWARDROW pRoute);

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    private static partial int SetIpForwardEntry(ref MIB_IPFORWARDROW pRoute);

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    private static partial int DeleteIpForwardEntry(ref MIB_IPFORWARDROW pRoute);

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    private static partial uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        int tableClass,
        uint reserved);

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    private static partial uint GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        int tableClass,
        uint reserved);

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DnsFlushResolverCache();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint processAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, IntPtr lpExeName, ref uint lpdwSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    private static readonly ConcurrentDictionary<uint, MIB_IPFORWARDROW> t_pinnedRoutes = new();
    private static readonly ConcurrentDictionary<uint, string> t_routeReasons = new();
    private static readonly ConcurrentDictionary<uint, (string ExeName, string FullPath, long TimestampTicks)> t_pidCache = new();
    private static readonly ConcurrentDictionary<uint, (bool IsBypassed, string ExeName, long TimestampTicks)> t_portCache = new();
    private static readonly ConcurrentDictionary<ulong, byte> t_checkedUdpFlows = new();
    private static readonly SemaphoreSlim t_reloadGate = new(1, 1);

    private static void NotifyRoutesUpdatedAsync() =>
        _ = ThreadPool.UnsafeQueueUserWorkItem(static _ => OnRoutesUpdated?.Invoke(), null);

    private static volatile uint t_physicalGatewayUint;
    private static volatile int t_physicalIfIndex;
    private static volatile uint t_baseMetric = 25;
    private static IPAddress? t_physicalLocalIp;
    private static volatile uint t_vpnServerIpUint;

    public static int ActiveRouteCount => t_pinnedRoutes.Count;

    public static IReadOnlyList<(string DestinationIp, string Reason)> ActiveBypassRoutes
    {
        get
        {
            var keys = t_pinnedRoutes.Keys.ToArray();
            if (keys.Length == 0)
            {
                return [];
            }

            var list = new List<(string DestinationIp, string Reason)>(keys.Length);
            Span<byte> ipBytes = stackalloc byte[4];
            foreach (var ipUint in keys)
            {
                MemoryMarshal.Write(ipBytes, in ipUint);
                var ipStr = new IPAddress(ipBytes).ToString();
                var reason = t_routeReasons.TryGetValue(ipUint, out var r) && !string.IsNullOrWhiteSpace(r)
                    ? r
                    : ipStr;
                list.Add((ipStr, reason));
            }

            return list;
        }
    }

    public static void ConfigurePhysicalGateway(string gatewayIp, int physicalIfIndex, IPAddress? physicalLocalIp, string? vpnServerIp)
    {
        if (string.IsNullOrWhiteSpace(gatewayIp) || !IPAddress.TryParse(gatewayIp, out var gwAddr) || gwAddr.AddressFamily != AddressFamily.InterNetwork)
        {
            return;
        }

        var gwUint = MemoryMarshal.Read<uint>(gwAddr.GetAddressBytes());
        t_physicalGatewayUint = gwUint;
        t_physicalIfIndex = physicalIfIndex;
        t_physicalLocalIp = physicalLocalIp;

        if (!string.IsNullOrWhiteSpace(vpnServerIp) && IPAddress.TryParse(vpnServerIp, out var srvAddr) && srvAddr.AddressFamily == AddressFamily.InterNetwork)
        {
            t_vpnServerIpUint = MemoryMarshal.Read<uint>(srvAddr.GetAddressBytes());
        }

        t_baseMetric = t_vpnServerIpUint != 0 && GetBestRoute(t_vpnServerIpUint, 0, out var srvRow) == 0 && srvRow.DwForwardMetric1 > 0
            ? srvRow.DwForwardMetric1
            : GetBestRoute(gwUint, 0, out var gwRow) == 0 && gwRow.DwForwardMetric1 > 0
                ? gwRow.DwForwardMetric1
                : 25;
    }

    public static async Task ActivateOrReloadAsync()
    {
        if (!await t_reloadGate.WaitAsync(3000).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var policy = Policy;
            t_checkedUdpFlows.Clear();
            t_portCache.Clear();

            if (!policy.Enabled || t_physicalGatewayUint == 0)
            {
                CleanupAllRoutesInternal();
                NotifyRoutesUpdatedAsync();
                return;
            }

            PruneStaleRoutes(policy);

            foreach (var rawIp in policy.CustomBypassIps.ToArray())
            {
                if (IPAddress.TryParse(rawIp, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    var ipUint = MemoryMarshal.Read<uint>(ip.GetAddressBytes());
                    PinPhysicalHostRoute(ipUint, rawIp, notify: false);
                }
            }

            if (policy.HasBypassedExecutables)
            {
                PinActiveProcessEndpoints(notify: false);
            }

            if (policy.HasDomainRules)
            {
                var seeds = policy.GetSeedDomainsToPreResolve();
                if (seeds.Count > 0)
                {
                    await PreResolveSeedDomainsOnPhysicalAsync(seeds).ConfigureAwait(false);
                }
            }

            _ = DnsFlushResolverCache();
            NotifyRoutesUpdatedAsync();
        }
        finally
        {
            _ = t_reloadGate.Release();
        }
    }

    private static void PruneStaleRoutes(SplitTunnelPolicy policy)
    {
        var customIps = policy.CustomBypassIps.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedAny = false;
        Span<byte> ipBytes = stackalloc byte[4];

        foreach (var kvp in t_routeReasons.ToArray())
        {
            var ipUint = kvp.Key;
            var reason = kvp.Value;

            MemoryMarshal.Write(ipBytes, in ipUint);
            var ipStr = new IPAddress(ipBytes).ToString();

            var stillValid = customIps.Contains(ipStr) ||
                             customIps.Contains(reason) ||
                             (reason.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && policy.IsProcessBypassed(reason, reason)) ||
                             (!reason.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && policy.IsDomainBypassed(reason));

            if (!stillValid)
            {
                _ = t_routeReasons.TryRemove(ipUint, out _);
                if (t_pinnedRoutes.TryRemove(ipUint, out var row))
                {
                    _ = DeleteIpForwardEntry(ref row);
                }
                removedAny = true;
            }
        }

        if (removedAny)
        {
            Policy.ClearActiveRoutes();
            foreach (var kvp in t_routeReasons)
            {
                var ipUint = kvp.Key;
                MemoryMarshal.Write(ipBytes, in ipUint);
                var ipStr = new IPAddress(ipBytes).ToString();
                Policy.RecordBypassRoute(ipStr, kvp.Value);
            }
        }
    }

    public static void PinPhysicalHostRoute(uint destIpNetworkOrder, string reason, bool notify = true)
    {
        if (t_physicalGatewayUint == 0 || !IsRoutablePublicIpv4(destIpNetworkOrder))
        {
            return;
        }

        t_routeReasons[destIpNetworkOrder] = reason;

        if (t_pinnedRoutes.ContainsKey(destIpNetworkOrder))
        {
            return;
        }

        var row = new MIB_IPFORWARDROW
        {
            DwForwardDest = destIpNetworkOrder,
            DwForwardMask = 0xFFFFFFFFu,
            DwForwardPolicy = 0,
            DwForwardNextHop = t_physicalGatewayUint,
            DwForwardIfIndex = (uint)Math.Max(t_physicalIfIndex, 0),
            DwForwardType = 4,
            DwForwardProto = 3,
            DwForwardAge = 0,
            DwForwardNextHopAS = 0,
            DwForwardMetric1 = Math.Max(t_baseMetric, 1u),
            DwForwardMetric2 = uint.MaxValue,
            DwForwardMetric3 = uint.MaxValue,
            DwForwardMetric4 = uint.MaxValue,
            DwForwardMetric5 = uint.MaxValue
        };

        uint[] candidateMetrics = [Math.Max(t_baseMetric, 1u), 25u, 35u, 50u, 100u];
        var status = -1;
        for (var i = 0; i < candidateMetrics.Length; i++)
        {
            row.DwForwardMetric1 = candidateMetrics[i];
            status = CreateIpForwardEntry(ref row);
            if (status == 5010)
            {
                status = SetIpForwardEntry(ref row);
            }
            if (status == 0)
            {
                break;
            }
        }

        t_pinnedRoutes[destIpNetworkOrder] = row;

        Span<byte> ipBytes = stackalloc byte[4];
        MemoryMarshal.Write(ipBytes, in destIpNetworkOrder);
        var ipStr = new IPAddress(ipBytes).ToString();
        Policy.RecordBypassRoute(ipStr, reason);
        if (notify)
        {
            NotifyRoutesUpdatedAsync();
        }

        if (status != 0)
        {
            Span<byte> gwBytes = stackalloc byte[4];
            var gwUint = t_physicalGatewayUint;
            MemoryMarshal.Write(gwBytes, in gwUint);
            var gwStr = new IPAddress(gwBytes).ToString();
            var ifIdx = t_physicalIfIndex;
            _ = Task.Run(() =>
            {
                try
                {
                    var ifArg = ifIdx > 0 ? $" if {ifIdx}" : "";
                    using var p = Process.Start(new ProcessStartInfo("route", $"add {ipStr} mask 255.255.255.255 {gwStr} metric 1{ifArg}")
                    {
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        UseShellExecute = false
                    });
                    _ = p?.WaitForExit(1500);
                }
                catch { }
            });
        }
    }

    public static void CleanupAllRoutes()
    {
        CleanupAllRoutesInternal();
        NotifyRoutesUpdatedAsync();
    }

    private static void CleanupAllRoutesInternal()
    {
        foreach (var kvp in t_pinnedRoutes)
        {
            var row = kvp.Value;
            _ = DeleteIpForwardEntry(ref row);
        }

        t_pinnedRoutes.Clear();
        t_routeReasons.Clear();
        t_checkedUdpFlows.Clear();
        t_portCache.Clear();
        Policy.ClearActiveRoutes();
    }

    public static void InspectIncomingPacketFromVpn(ReadOnlySpan<byte> packet, int length)
    {
        if (length < 40 || t_physicalGatewayUint == 0)
        {
            return;
        }

        var policy = Policy;
        if (!policy.Enabled || !policy.HasDomainRules)
        {
            return;
        }

        var version = packet[0] >> 4;
        if (version == 4)
        {
            var ihl = (packet[0] & 0x0F) * 4;
            if (ihl < 20 || length < ihl + 20 || packet[9] != 17)
            {
                return;
            }

            var srcPort = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(ihl, 2));
            if (srcPort != 53)
            {
                return;
            }

            var dnsOffset = ihl + 8;
            var dnsSpan = packet[dnsOffset..length];
            PinBypassedDnsAnswerIps(dnsSpan, policy);
        }
        else if (version == 6)
        {
            if (length < 60 || packet[6] != 17)
            {
                return;
            }

            var srcPort6 = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(40, 2));
            if (srcPort6 != 53)
            {
                return;
            }

            var dnsSpan6 = packet[48..length];
            PinBypassedDnsAnswerIps(dnsSpan6, policy);
        }
    }

    public static bool TryInterceptOutgoingPacket(
        byte[] packetBuffer,
        int length,
        WintunAdapter adapter)
    {
        if (length < 28 || t_physicalGatewayUint == 0)
        {
            return false;
        }

        var policy = Policy;
        if (!policy.Enabled)
        {
            return false;
        }

        var hasApps = policy.HasBypassedExecutables;
        var hasDomains = policy.HasDomainRules;
        if (!hasApps && !hasDomains && policy.CustomBypassIps.Count == 0)
        {
            return false;
        }

        var packet = packetBuffer.AsSpan(0, length);
        var version = packet[0] >> 4;

        if (version == 6)
        {
            if (length < 48)
            {
                return false;
            }

            var nextHdr = packet[6];
            var srcPort6 = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(40, 2));
            var dstPort6 = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(42, 2));

            if (nextHdr == 17 && dstPort6 == 53)
            {
                var dnsSpan6 = packet[48..];
                if (TryReadDnsQuestion(dnsSpan6, out var qName6, out var qType6, out var qNameLen6))
                {
                    var isDomainMatch = hasDomains && policy.IsDomainBypassed(qName6);
                    var isAppMatch = !isDomainMatch && hasApps && IsPortOwnedByBypassedProcess(false, srcPort6, out _, isIpv6: true);
                    if (isDomainMatch || isAppMatch)
                    {
                        if (qType6 == 28)
                        {
                            var nodata6 = CreateDnsNoDataResponse(packetBuffer, 40, 40, 48, qNameLen6, false);
                            adapter.SendPacket(nodata6);
                            return true;
                        }

                        if (qType6 == 1)
                        {
                            var reqCopy6 = new byte[length];
                            Buffer.BlockCopy(packetBuffer, 0, reqCopy6, 0, length);
                            _ = Task.Run(() => ForwardBypassedDnsQueryOnPhysicalAsync(reqCopy6, 40, 48, qName6, adapter, isIpv4Packet: false));
                            return true;
                        }
                    }
                }
                return false;
            }

            if (nextHdr == 6 && length >= 60)
            {
                if (hasApps && IsPortOwnedByBypassedProcess(true, srcPort6, out _, isIpv6: true))
                {
                    SendIpv6TcpReset(packet, adapter);
                    return true;
                }

                var tcpHdrLen6 = ((packet[52] >> 4) & 0x0F) * 4;
                var payloadOffset6 = 40 + tcpHdrLen6;
                var payloadLen6 = length - payloadOffset6;
                if (hasDomains && payloadLen6 > 5)
                {
                    var payload6 = packet.Slice(payloadOffset6, payloadLen6);
                    if ((TryExtractTlsSni(payload6, out var host6) || TryExtractHttpHost(payload6, out host6)) &&
                        policy.IsDomainBypassed(host6))
                    {
                        SendIpv6TcpReset(packet, adapter);
                        return true;
                    }
                }
            }
            else if (nextHdr == 17 && hasApps)
            {
                if (IsPortOwnedByBypassedProcess(false, srcPort6, out _, isIpv6: true))
                {
                    return true;
                }
            }

            return false;
        }

        if (version != 4)
        {
            return false;
        }

        var ihl = (packet[0] & 0x0F) * 4;
        if (ihl < 20 || length < ihl + 8)
        {
            return false;
        }

        var protocol = packet[9];
        var dstIpUint = MemoryMarshal.Read<uint>(packet.Slice(16, 4));

        if (protocol == 17)
        {
            var srcPort = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(ihl, 2));
            var dstPort = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(ihl + 2, 2));

            if (dstPort == 53 && length >= ihl + 20)
            {
                var dnsOffset = ihl + 8;
                var dnsSpan = packet[dnsOffset..];
                if (TryReadDnsQuestion(dnsSpan, out var qName, out var qType, out var qNameLen))
                {
                    var isDomainBypassed = hasDomains && policy.IsDomainBypassed(qName);
                    string? dnsOwnerExe = null;
                    var isAppBypassed = !isDomainBypassed && hasApps && IsPortOwnedByBypassedProcess(false, srcPort, out dnsOwnerExe);
                    if (isDomainBypassed || isAppBypassed)
                    {
                        if (qType == 28)
                        {
                            var nodata = CreateDnsNoDataResponse(packetBuffer, ihl, ihl, dnsOffset, qNameLen, true);
                            adapter.SendPacket(nodata);
                            return true;
                        }

                        if (qType == 1)
                        {
                            var reasonLabel = isDomainBypassed ? qName : (dnsOwnerExe ?? qName);
                            var reqCopy = new byte[length];
                            Buffer.BlockCopy(packetBuffer, 0, reqCopy, 0, length);
                            _ = Task.Run(() => ForwardBypassedDnsQueryOnPhysicalAsync(reqCopy, ihl, dnsOffset, reasonLabel, adapter, isIpv4Packet: true));
                            return true;
                        }
                    }
                }
                return false;
            }

            if (hasApps && IsRoutablePublicIpv4(dstIpUint) && !t_pinnedRoutes.ContainsKey(dstIpUint))
            {
                var flowKey = ((ulong)srcPort << 32) | dstIpUint;
                if (t_checkedUdpFlows.TryAdd(flowKey, 1))
                {
                    if (IsPortOwnedByBypassedProcess(false, srcPort, out var exeName))
                    {
                        PinPhysicalHostRoute(dstIpUint, exeName);
                        return true;
                    }
                }
            }

            return false;
        }

        if (protocol == 6)
        {
            if (length < ihl + 20 || !IsRoutablePublicIpv4(dstIpUint))
            {
                return false;
            }

            var srcPort = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(ihl, 2));
            var tcpFlags = packet[ihl + 13];
            var isSyn = (tcpFlags & 0x02) != 0 && (tcpFlags & 0x10) == 0;

            if (isSyn)
            {
                if (t_pinnedRoutes.ContainsKey(dstIpUint))
                {
                    SendTcpSynAck(packet, ihl, adapter);
                    return true;
                }

                if (hasApps && IsPortOwnedByBypassedProcess(true, srcPort, out var exeName))
                {
                    PinPhysicalHostRoute(dstIpUint, exeName);
                    SendTcpSynAck(packet, ihl, adapter);
                    return true;
                }

                return false;
            }

            var tcpHeaderLen = ((packet[ihl + 12] >> 4) & 0x0F) * 4;
            var payloadOffset = ihl + tcpHeaderLen;
            var payloadLen = length - payloadOffset;

            if (payloadLen == 0 && t_pinnedRoutes.ContainsKey(dstIpUint))
            {
                return true;
            }

            if (payloadLen > 0)
            {
                if (t_pinnedRoutes.ContainsKey(dstIpUint))
                {
                    SendTcpReset(packet, ihl, adapter);
                    return true;
                }

                if (payloadLen > 5)
                {
                    var payload = packet.Slice(payloadOffset, payloadLen);
                    if (hasDomains)
                    {
                        if ((TryExtractTlsSni(payload, out var host) || TryExtractHttpHost(payload, out host)) &&
                            policy.IsDomainBypassed(host))
                        {
                            PinPhysicalHostRoute(dstIpUint, host);
                            SendTcpReset(packet, ihl, adapter);
                            return true;
                        }
                    }

                    if (hasApps && (payload[0] == 0x16 || payload[0] == (byte)'G' || payload[0] == (byte)'P'))
                    {
                        if (IsPortOwnedByBypassedProcess(true, srcPort, out var exeName))
                        {
                            PinPhysicalHostRoute(dstIpUint, exeName);
                            SendTcpReset(packet, ihl, adapter);
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    private static async Task ForwardBypassedDnsQueryOnPhysicalAsync(
        byte[] reqPacket,
        int ipHdrLen,
        int dnsOffset,
        string reasonLabel,
        WintunAdapter adapter,
        bool isIpv4Packet)
    {
        try
        {
            var dnsPayloadLen = reqPacket.Length - dnsOffset;
            var dnsPayload = new byte[dnsPayloadLen];
            Buffer.BlockCopy(reqPacket, dnsOffset, dnsPayload, 0, dnsPayloadLen);

            var respBytes = await QueryPhysicalDnsRawAsync(dnsPayload).ConfigureAwait(false);
            if (respBytes is { Length: >= 12 })
            {
                PinBypassedDnsAnswerIps(respBytes, Policy, reasonLabel, forcePinAllAnswers: true);
                var wrapped = isIpv4Packet
                    ? WrapDnsResponsePacket(reqPacket, ipHdrLen, respBytes)
                    : WrapIpv6DnsResponsePacket(reqPacket, respBytes);
                adapter.SendPacket(wrapped);
            }
        }
        catch { }
    }

    private static async Task<byte[]?> QueryPhysicalDnsRawAsync(byte[] dnsPayload)
    {
        Span<byte> gwBytes = stackalloc byte[4];
        var gwUint = t_physicalGatewayUint;
        MemoryMarshal.Write(gwBytes, in gwUint);
        var gwIp = new IPAddress(gwBytes);

        IPAddress[] dnsServers = [IPAddress.Parse("77.88.8.8"), gwIp, IPAddress.Parse("77.88.8.1")];

        foreach (var dnsIp in dnsServers)
        {
            if (dnsIp.Equals(IPAddress.Any))
            {
                continue;
            }

            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                if (t_physicalLocalIp is not null && !IPAddress.IsLoopback(t_physicalLocalIp))
                {
                    try
                    {
                        socket.Bind(new IPEndPoint(t_physicalLocalIp, 0));
                    }
                    catch { }
                }

                using var cts = new CancellationTokenSource(1200);
                var dnsTarget = new IPEndPoint(dnsIp, 53);
                _ = await socket.SendToAsync(dnsPayload, SocketFlags.None, dnsTarget, cts.Token).ConfigureAwait(false);

                var recvBuf = ArrayPool<byte>.Shared.Rent(1500);
                try
                {
                    var res = await socket.ReceiveFromAsync(recvBuf.AsMemory(0, 1450), SocketFlags.None, dnsTarget, cts.Token).ConfigureAwait(false);
                    if (res.ReceivedBytes >= 12)
                    {
                        var result = new byte[res.ReceivedBytes];
                        Buffer.BlockCopy(recvBuf, 0, result, 0, res.ReceivedBytes);
                        return result;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(recvBuf);
                }
            }
            catch { }
        }

        return null;
    }

    private static byte[] BuildDnsAQueryPayload(string domain)
    {
        var labels = domain.Trim('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        using var ms = new MemoryStream(64);
        Span<byte> hdr = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(hdr[..2], (ushort)Random.Shared.Next(1, 65535));
        BinaryPrimitives.WriteUInt16BigEndian(hdr.Slice(2, 2), 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(hdr.Slice(4, 2), 1);
        ms.Write(hdr);

        foreach (var label in labels)
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        ms.WriteByte(0);

        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(tail[..2], 1);
        BinaryPrimitives.WriteUInt16BigEndian(tail.Slice(2, 2), 1);
        ms.Write(tail);
        return ms.ToArray();
    }

    private static async Task PreResolveSeedDomainsOnPhysicalAsync(List<string> domains)
    {
        try
        {
            using var sem = new SemaphoreSlim(8, 8);
            var tasks = domains.Select(async domain =>
            {
                await sem.WaitAsync().ConfigureAwait(false);
                try
                {
                    var query = BuildDnsAQueryPayload(domain);
                    var resp = await QueryPhysicalDnsRawAsync(query).ConfigureAwait(false);
                    if (resp is { Length: >= 12 })
                    {
                        PinBypassedDnsAnswerIps(resp, Policy, domain, forcePinAllAnswers: true, notify: false);
                    }
                }
                catch { }
                finally
                {
                    _ = sem.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch { }
    }

    public static unsafe void PinActiveProcessEndpoints(bool notify = true)
    {
        var policy = Policy;
        if (!policy.Enabled || !policy.HasBypassedExecutables || t_physicalGatewayUint == 0)
        {
            return;
        }

        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        if (size <= 0)
        {
            return;
        }

        var addedAny = false;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0) == 0)
            {
                var count = *(int*)buffer;
                var rows = (MIB_TCPROW_OWNER_PID*)(buffer + 4);

                for (var i = 0; i < count; i++)
                {
                    ref readonly var row = ref rows[i];

                    if (row.RemoteAddr == 0 || !IsRoutablePublicIpv4(row.RemoteAddr))
                    {
                        continue;
                    }

                    if (TryGetProcessInfo(row.OwningPid, out var exeName, out var fullPath) &&
                        policy.IsProcessBypassed(exeName, fullPath))
                    {
                        PinPhysicalHostRoute(row.RemoteAddr, exeName, notify: false);
                        addedAny = true;
                    }
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        if (notify && addedAny)
        {
            NotifyRoutesUpdatedAsync();
        }
    }

    private static unsafe bool IsPortOwnedByBypassedProcess(bool isTcp, ushort localPortHostOrder, out string matchedExeName, bool isIpv6 = false)
    {
        matchedExeName = string.Empty;
        var policy = Policy;
        if (!policy.Enabled || !policy.HasBypassedExecutables)
        {
            return false;
        }

        var cacheKey = ((uint)(isTcp ? 1 : 0) << 17) | ((uint)(isIpv6 ? 1 : 0) << 16) | localPortHostOrder;
        var nowTicks = Environment.TickCount64;
        if (t_portCache.TryGetValue(cacheKey, out var cachedPort) && nowTicks - cachedPort.TimestampTicks < 2000)
        {
            matchedExeName = cachedPort.ExeName;
            return cachedPort.IsBypassed;
        }

        var targetPortNetOrder = (uint)BinaryPrimitives.ReverseEndianness(localPortHostOrder);
        var af = isIpv6 ? AfInet6 : AfInet;

        if (isTcp)
        {
            var size = 0;
            _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TcpTableOwnerPidAll, 0);
            if (size <= 0)
            {
                return false;
            }

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, af, TcpTableOwnerPidAll, 0) == 0)
                {
                    var count = *(int*)buffer;
                    if (isIpv6)
                    {
                        var rows6 = (MIB_TCP6ROW_OWNER_PID*)(buffer + 4);
                        for (var i = 0; i < count; i++)
                        {
                            ref readonly var r = ref rows6[i];
                            if ((r.LocalPort & 0xFFFF) == targetPortNetOrder && r.OwningPid > 4)
                            {
                                var isBypassed = TryGetProcessInfo(r.OwningPid, out var exeName, out var fullPath) &&
                                                 policy.IsProcessBypassed(exeName, fullPath);
                                t_portCache[cacheKey] = (isBypassed, isBypassed ? exeName : string.Empty, nowTicks);
                                if (isBypassed)
                                {
                                    matchedExeName = exeName;
                                    return true;
                                }
                                return false;
                            }
                        }
                    }
                    else
                    {
                        var rows4 = (MIB_TCPROW_OWNER_PID*)(buffer + 4);
                        for (var i = 0; i < count; i++)
                        {
                            ref readonly var r = ref rows4[i];
                            if ((r.LocalPort & 0xFFFF) == targetPortNetOrder && r.OwningPid > 4)
                            {
                                var isBypassed = TryGetProcessInfo(r.OwningPid, out var exeName, out var fullPath) &&
                                                 policy.IsProcessBypassed(exeName, fullPath);
                                t_portCache[cacheKey] = (isBypassed, isBypassed ? exeName : string.Empty, nowTicks);
                                if (isBypassed)
                                {
                                    matchedExeName = exeName;
                                    return true;
                                }
                                return false;
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
        else
        {
            var size = 0;
            _ = GetExtendedUdpTable(IntPtr.Zero, ref size, false, af, UdpTableOwnerPid, 0);
            if (size <= 0)
            {
                return false;
            }

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedUdpTable(buffer, ref size, false, af, UdpTableOwnerPid, 0) == 0)
                {
                    var count = *(int*)buffer;
                    if (isIpv6)
                    {
                        var rows6 = (MIB_UDP6ROW_OWNER_PID*)(buffer + 4);
                        for (var i = 0; i < count; i++)
                        {
                            ref readonly var r = ref rows6[i];
                            if ((r.LocalPort & 0xFFFF) == targetPortNetOrder && r.OwningPid > 4)
                            {
                                var isBypassed = TryGetProcessInfo(r.OwningPid, out var exeName, out var fullPath) &&
                                                 policy.IsProcessBypassed(exeName, fullPath);
                                t_portCache[cacheKey] = (isBypassed, isBypassed ? exeName : string.Empty, nowTicks);
                                if (isBypassed)
                                {
                                    matchedExeName = exeName;
                                    return true;
                                }
                                return false;
                            }
                        }
                    }
                    else
                    {
                        var rows4 = (MIB_UDPROW_OWNER_PID*)(buffer + 4);
                        for (var i = 0; i < count; i++)
                        {
                            ref readonly var r = ref rows4[i];
                            if ((r.LocalPort & 0xFFFF) == targetPortNetOrder && r.OwningPid > 4)
                            {
                                var isBypassed = TryGetProcessInfo(r.OwningPid, out var exeName, out var fullPath) &&
                                                 policy.IsProcessBypassed(exeName, fullPath);
                                t_portCache[cacheKey] = (isBypassed, isBypassed ? exeName : string.Empty, nowTicks);
                                if (isBypassed)
                                {
                                    matchedExeName = exeName;
                                    return true;
                                }
                                return false;
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

        t_portCache[cacheKey] = (false, string.Empty, nowTicks);
        return false;
    }

    private static unsafe bool TryGetProcessInfo(uint pid, out string exeName, out string fullPath)
    {
        var nowTicks = Environment.TickCount64;
        if (t_pidCache.TryGetValue(pid, out var cached) && nowTicks - cached.TimestampTicks < 15000)
        {
            exeName = cached.ExeName;
            fullPath = cached.FullPath;
            return !string.IsNullOrEmpty(exeName);
        }

        exeName = string.Empty;
        fullPath = string.Empty;

        var hProcess = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (hProcess == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var chars = stackalloc char[512];
            uint len = 512;
            if (QueryFullProcessImageNameW(hProcess, 0, (IntPtr)chars, ref len) && len > 0)
            {
                fullPath = new string(chars, 0, (int)len);
                exeName = Path.GetFileName(fullPath);
                t_pidCache[pid] = (exeName, fullPath, nowTicks);
                return true;
            }
        }
        finally
        {
            _ = CloseHandle(hProcess);
        }

        return false;
    }

    private static bool IsRoutablePublicIpv4(uint ipNetworkOrder)
    {
        if (ipNetworkOrder is 0 or 0xFFFFFFFFu || ipNetworkOrder == t_vpnServerIpUint)
        {
            return false;
        }

        var b0 = (byte)(ipNetworkOrder & 0xFF);
        var b1 = (byte)((ipNetworkOrder >> 8) & 0xFF);

        return b0 is not (10 or 127 or 0 or >= 224) &&
               !(b0 == 172 && b1 is >= 16 and <= 31) &&
               !(b0 == 192 && b1 == 168) &&
               !(b0 == 169 && b1 == 254) &&
               !(b0 == 100 && b1 is >= 64 and <= 127);
    }

    private static void SendTcpSynAck(ReadOnlySpan<byte> origPacket, int ihl, WintunAdapter adapter)
    {
        if (origPacket.Length < ihl + 20)
        {
            return;
        }

        var synAck = new byte[40];
        synAck[0] = 0x45;
        synAck[1] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(synAck.AsSpan(2, 2), 40);
        synAck[4] = 0;
        synAck[5] = 1;
        synAck[6] = 0x40;
        synAck[7] = 0;
        synAck[8] = 64;
        synAck[9] = 6;

        origPacket.Slice(16, 4).CopyTo(synAck.AsSpan(12, 4));
        origPacket.Slice(12, 4).CopyTo(synAck.AsSpan(16, 4));

        origPacket.Slice(ihl + 2, 2).CopyTo(synAck.AsSpan(20, 2));
        origPacket.Slice(ihl, 2).CopyTo(synAck.AsSpan(22, 2));

        var incomingSeq = BinaryPrimitives.ReadUInt32BigEndian(origPacket.Slice(ihl + 4, 4));
        BinaryPrimitives.WriteUInt32BigEndian(synAck.AsSpan(24, 4), 0x10203040u);
        BinaryPrimitives.WriteUInt32BigEndian(synAck.AsSpan(28, 4), incomingSeq + 1u);
        synAck[32] = 0x50;
        synAck[33] = 0x12;
        BinaryPrimitives.WriteUInt16BigEndian(synAck.AsSpan(34, 2), 65535);

        ComputeIpv4AndTcpChecksum(synAck);
        adapter.SendPacket(synAck);
    }

    private static void SendTcpReset(ReadOnlySpan<byte> origPacket, int ihl, WintunAdapter adapter)
    {
        if (origPacket.Length < ihl + 20)
        {
            return;
        }

        var rst = new byte[40];
        rst[0] = 0x45;
        rst[1] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(rst.AsSpan(2, 2), 40);
        rst[4] = 0;
        rst[5] = 1;
        rst[6] = 0x40;
        rst[7] = 0;
        rst[8] = 64;
        rst[9] = 6;

        origPacket.Slice(16, 4).CopyTo(rst.AsSpan(12, 4));
        origPacket.Slice(12, 4).CopyTo(rst.AsSpan(16, 4));

        origPacket.Slice(ihl + 2, 2).CopyTo(rst.AsSpan(20, 2));
        origPacket.Slice(ihl, 2).CopyTo(rst.AsSpan(22, 2));

        var incomingSeq = BinaryPrimitives.ReadUInt32BigEndian(origPacket.Slice(ihl + 4, 4));
        var incomingAck = BinaryPrimitives.ReadUInt32BigEndian(origPacket.Slice(ihl + 8, 4));
        var incomingFlags = origPacket[ihl + 13];

        var tcpHeaderLen = ((origPacket[ihl + 12] >> 4) & 0x0F) * 4;
        var payloadLen = Math.Max(origPacket.Length - ihl - tcpHeaderLen, 0);
        var ackAdvance = (uint)payloadLen + ((incomingFlags & 0x02) != 0 ? 1u : 0u);

        BinaryPrimitives.WriteUInt32BigEndian(rst.AsSpan(24, 4), incomingAck);
        BinaryPrimitives.WriteUInt32BigEndian(rst.AsSpan(28, 4), incomingSeq + Math.Max(ackAdvance, 1u));
        rst[32] = 0x50;
        rst[33] = 0x14;
        rst[34] = 0;
        rst[35] = 0;

        ComputeIpv4AndTcpChecksum(rst);
        adapter.SendPacket(rst);
    }

    private static void SendIpv6TcpReset(ReadOnlySpan<byte> origPacket, WintunAdapter adapter)
    {
        if (origPacket.Length < 60)
        {
            return;
        }

        var rst = new byte[60];
        rst[0] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(rst.AsSpan(4, 2), 20);
        rst[6] = 6;
        rst[7] = 64;

        origPacket.Slice(24, 16).CopyTo(rst.AsSpan(8, 16));
        origPacket.Slice(8, 16).CopyTo(rst.AsSpan(24, 16));

        origPacket.Slice(42, 2).CopyTo(rst.AsSpan(40, 2));
        origPacket.Slice(40, 2).CopyTo(rst.AsSpan(42, 2));

        var incomingSeq = BinaryPrimitives.ReadUInt32BigEndian(origPacket.Slice(44, 4));
        var incomingAck = BinaryPrimitives.ReadUInt32BigEndian(origPacket.Slice(48, 4));
        var incomingFlags = origPacket[53];
        var tcpHeaderLen = ((origPacket[52] >> 4) & 0x0F) * 4;
        var payloadLen = Math.Max(origPacket.Length - 40 - tcpHeaderLen, 0);
        var ackAdvance = (uint)payloadLen + ((incomingFlags & 0x02) != 0 ? 1u : 0u);

        BinaryPrimitives.WriteUInt32BigEndian(rst.AsSpan(44, 4), incomingAck);
        BinaryPrimitives.WriteUInt32BigEndian(rst.AsSpan(48, 4), incomingSeq + Math.Max(ackAdvance, 1u));
        rst[52] = 0x50;
        rst[53] = 0x14;

        uint tcpSum = 0;
        for (var i = 8; i < 40; i += 2)
        {
            tcpSum += (uint)((rst[i] << 8) | rst[i + 1]);
        }
        tcpSum += 20u;
        tcpSum += 6u;
        for (var i = 40; i < 60; i += 2)
        {
            tcpSum += (uint)((rst[i] << 8) | rst[i + 1]);
        }
        while ((tcpSum >> 16) != 0)
        {
            tcpSum = (tcpSum & 0xFFFF) + (tcpSum >> 16);
        }
        tcpSum = ~tcpSum;
        rst[56] = (byte)(tcpSum >> 8);
        rst[57] = (byte)(tcpSum & 0xFF);

        adapter.SendPacket(rst);
    }

    private static void ComputeIpv4AndTcpChecksum(byte[] pkt)
    {
        uint ipSum = 0;
        for (var i = 0; i < 20; i += 2)
        {
            ipSum += (uint)((pkt[i] << 8) | pkt[i + 1]);
        }
        while ((ipSum >> 16) != 0)
        {
            ipSum = (ipSum & 0xFFFF) + (ipSum >> 16);
        }
        ipSum = ~ipSum;
        pkt[10] = (byte)(ipSum >> 8);
        pkt[11] = (byte)(ipSum & 0xFF);

        uint tcpSum = 0;
        for (var i = 12; i < 20; i += 2)
        {
            tcpSum += (uint)((pkt[i] << 8) | pkt[i + 1]);
        }
        tcpSum += 6u;
        tcpSum += 20u;

        for (var i = 20; i < 40; i += 2)
        {
            tcpSum += (uint)((pkt[i] << 8) | pkt[i + 1]);
        }
        while ((tcpSum >> 16) != 0)
        {
            tcpSum = (tcpSum & 0xFFFF) + (tcpSum >> 16);
        }
        tcpSum = ~tcpSum;
        pkt[36] = (byte)(tcpSum >> 8);
        pkt[37] = (byte)(tcpSum & 0xFF);
    }

    private static bool TryExtractTlsSni(ReadOnlySpan<byte> payload, out string sniHost)
    {
        sniHost = string.Empty;
        if (payload.Length < 43 || payload[0] != 0x16 || payload[1] != 0x03 || payload[5] != 0x01)
        {
            return false;
        }

        var pos = 43;
        if (pos >= payload.Length)
        {
            return false;
        }

        int sessionIdLen = payload[pos];
        pos += 1 + sessionIdLen;
        if (pos + 2 > payload.Length)
        {
            return false;
        }

        int cipherSuitesLen = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(pos, 2));
        pos += 2 + cipherSuitesLen;
        if (pos + 1 > payload.Length)
        {
            return false;
        }

        int compMethodsLen = payload[pos];
        pos += 1 + compMethodsLen;
        if (pos + 2 > payload.Length)
        {
            return false;
        }

        int extensionsTotalLen = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(pos, 2));
        pos += 2;
        var extensionsEnd = Math.Min(pos + extensionsTotalLen, payload.Length);

        while (pos + 4 <= extensionsEnd)
        {
            var extType = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(pos, 2));
            var extLen = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(pos + 2, 2));
            pos += 4;

            if (extType == 0x0000 && extLen >= 5 && pos + extLen <= extensionsEnd)
            {
                var listPos = pos + 2;
                var nameType = payload[listPos];
                var nameLen = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(listPos + 1, 2));
                if (nameType == 0 && listPos + 3 + nameLen <= pos + extLen && nameLen is > 0 and <= 253)
                {
                    sniHost = Encoding.ASCII.GetString(payload.Slice(listPos + 3, nameLen));
                    return true;
                }
                break;
            }

            if (pos + extLen > extensionsEnd)
            {
                break;
            }

            pos += extLen;
        }

        return false;
    }

    private static bool TryExtractHttpHost(ReadOnlySpan<byte> payload, out string host)
    {
        host = string.Empty;
        if (payload.Length < 16)
        {
            return false;
        }

        if (!payload.StartsWith("GET "u8) && !payload.StartsWith("POST "u8) && !payload.StartsWith("HEAD "u8))
        {
            return false;
        }

        var headerLimit = Math.Min(payload.Length, 512);
        var slice = payload[..headerLimit];
        var hostMarker = "\r\nHost:"u8;
        var idx = slice.IndexOf(hostMarker);
        if (idx < 0)
        {
            hostMarker = "\r\nhost:"u8;
            idx = slice.IndexOf(hostMarker);
        }

        if (idx < 0)
        {
            return false;
        }

        var start = idx + hostMarker.Length;
        while (start < slice.Length && (slice[start] == (byte)' ' || slice[start] == (byte)'\t'))
        {
            start++;
        }

        var end = start;
        while (end < slice.Length && slice[end] != (byte)'\r' && slice[end] != (byte)'\n' && slice[end] != (byte)':')
        {
            end++;
        }

        if (end > start && end - start <= 253)
        {
            host = Encoding.ASCII.GetString(slice[start..end]);
            return true;
        }

        return false;
    }

    private static bool TryReadDnsQuestion(ReadOnlySpan<byte> dnsSpan, out string qName, out ushort qType, out int qNameLen)
    {
        qName = string.Empty;
        qType = 0;
        qNameLen = 0;

        if (dnsSpan.Length < 17)
        {
            return false;
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(dnsSpan.Slice(2, 2));
        var qdCount = BinaryPrimitives.ReadUInt16BigEndian(dnsSpan.Slice(4, 2));
        if ((flags & 0x8000) != 0 || qdCount == 0)
        {
            return false;
        }

        qName = ReadDnsDomainName(dnsSpan, 12, out qNameLen);
        if (string.IsNullOrEmpty(qName) || 12 + qNameLen + 4 > dnsSpan.Length)
        {
            return false;
        }

        qType = BinaryPrimitives.ReadUInt16BigEndian(dnsSpan.Slice(12 + qNameLen, 2));
        return true;
    }

    private static void PinBypassedDnsAnswerIps(ReadOnlySpan<byte> dnsSpan, SplitTunnelPolicy policy, string? knownQueryName = null, bool forcePinAllAnswers = false, bool notify = true)
    {
        if (dnsSpan.Length < 12)
        {
            return;
        }

        var qdCount = BinaryPrimitives.ReadUInt16BigEndian(dnsSpan.Slice(4, 2));
        var anCount = BinaryPrimitives.ReadUInt16BigEndian(dnsSpan.Slice(6, 2));
        if (anCount == 0)
        {
            return;
        }

        var offset = 12;
        var primaryDomain = knownQueryName;

        for (var q = 0; q < qdCount; q++)
        {
            var name = ReadDnsDomainName(dnsSpan, offset, out var read);
            if (read <= 0)
            {
                return;
            }
            primaryDomain ??= name;
            offset += read + 4;
            if (offset > dnsSpan.Length)
            {
                return;
            }
        }

        if (string.IsNullOrEmpty(primaryDomain))
        {
            return;
        }

        if (!forcePinAllAnswers && !policy.IsDomainBypassed(primaryDomain))
        {
            return;
        }

        var addedAny = false;
        for (var a = 0; a < anCount; a++)
        {
            if (offset >= dnsSpan.Length)
            {
                break;
            }

            _ = ReadDnsDomainName(dnsSpan, offset, out var nameLen);
            if (nameLen <= 0 || offset + nameLen + 10 > dnsSpan.Length)
            {
                break;
            }

            offset += nameLen;
            var rType = BinaryPrimitives.ReadUInt16BigEndian(dnsSpan.Slice(offset, 2));
            var rdLen = BinaryPrimitives.ReadUInt16BigEndian(dnsSpan.Slice(offset + 8, 2));
            offset += 10;

            if (offset + rdLen > dnsSpan.Length)
            {
                break;
            }

            if (rType == 1 && rdLen == 4)
            {
                var ipUint = MemoryMarshal.Read<uint>(dnsSpan.Slice(offset, 4));
                PinPhysicalHostRoute(ipUint, primaryDomain, notify: false);
                addedAny = true;
            }

            offset += rdLen;
        }

        if (notify && addedAny)
        {
            OnRoutesUpdated?.Invoke();
        }
    }

    private static string ReadDnsDomainName(ReadOnlySpan<byte> dnsSpan, int startOffset, out int bytesConsumed)
    {
        bytesConsumed = 0;
        if (startOffset >= dnsSpan.Length)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(64);
        var current = startOffset;
        var jumped = false;
        var jumps = 0;

        while (current < dnsSpan.Length && jumps < 10)
        {
            var len = dnsSpan[current];
            if (len == 0)
            {
                if (!jumped)
                {
                    bytesConsumed = current - startOffset + 1;
                }
                break;
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (current + 1 >= dnsSpan.Length)
                {
                    return string.Empty;
                }

                if (!jumped)
                {
                    bytesConsumed = current - startOffset + 2;
                    jumped = true;
                }

                current = ((len & 0x3F) << 8) | dnsSpan[current + 1];
                jumps++;
                continue;
            }

            current++;
            if (current + len > dnsSpan.Length)
            {
                return string.Empty;
            }

            if (sb.Length > 0)
            {
                _ = sb.Append('.');
            }

            _ = sb.Append(Encoding.ASCII.GetString(dnsSpan.Slice(current, len)));
            current += len;
        }

        if (!jumped && bytesConsumed == 0)
        {
            bytesConsumed = current - startOffset;
        }

        return sb.ToString();
    }

    private static byte[] WrapDnsResponsePacket(byte[] req, int ipHdrLen, ReadOnlySpan<byte> dnsResponsePayload)
    {
        var totalLen = ipHdrLen + 8 + dnsResponsePayload.Length;
        var resp = new byte[totalLen];

        Buffer.BlockCopy(req, 0, resp, 0, ipHdrLen);
        Buffer.BlockCopy(req, 12, resp, 16, 4);
        Buffer.BlockCopy(req, 16, resp, 12, 4);
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(2, 2), (ushort)totalLen);
        resp[10] = 0;
        resp[11] = 0;

        Buffer.BlockCopy(req, ipHdrLen, resp, ipHdrLen + 2, 2);
        Buffer.BlockCopy(req, ipHdrLen + 2, resp, ipHdrLen, 2);
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(ipHdrLen + 4, 2), (ushort)(totalLen - ipHdrLen));
        resp[ipHdrLen + 6] = 0;
        resp[ipHdrLen + 7] = 0;

        dnsResponsePayload.CopyTo(resp.AsSpan(ipHdrLen + 8));

        uint sum = 0;
        for (var i = 0; i < ipHdrLen; i += 2)
        {
            sum += (uint)((resp[i] << 8) | resp[i + 1]);
        }
        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        sum = ~sum;
        resp[10] = (byte)(sum >> 8);
        resp[11] = (byte)(sum & 0xFF);

        return resp;
    }

    private static byte[] WrapIpv6DnsResponsePacket(byte[] req, ReadOnlySpan<byte> dnsResponsePayload)
    {
        var udpLen = 8 + dnsResponsePayload.Length;
        var totalLen = 40 + udpLen;
        var resp = new byte[totalLen];

        Buffer.BlockCopy(req, 0, resp, 0, 40);
        Buffer.BlockCopy(req, 8, resp, 24, 16);
        Buffer.BlockCopy(req, 24, resp, 8, 16);
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(4, 2), (ushort)udpLen);
        resp[6] = 17;
        resp[7] = 64;

        Buffer.BlockCopy(req, 40, resp, 42, 2);
        Buffer.BlockCopy(req, 42, resp, 40, 2);
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(44, 2), (ushort)udpLen);
        resp[46] = 0;
        resp[47] = 0;

        dnsResponsePayload.CopyTo(resp.AsSpan(48));

        uint sum = 0;
        for (var i = 8; i < 40; i += 2)
        {
            sum += (uint)((resp[i] << 8) | resp[i + 1]);
        }
        sum += (uint)udpLen;
        sum += 17u;
        for (var i = 0; i < udpLen; i += 2)
        {
            if (i + 1 < udpLen)
            {
                sum += (uint)((resp[40 + i] << 8) | resp[40 + i + 1]);
            }
            else
            {
                sum += (uint)(resp[40 + i] << 8);
            }
        }
        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        sum = ~sum;
        if (sum == 0)
        {
            sum = 0xFFFF;
        }
        resp[46] = (byte)(sum >> 8);
        resp[47] = (byte)(sum & 0xFF);

        return resp;
    }

    private static byte[] CreateDnsNoDataResponse(byte[] req, int ipHdrLen, int udpOffset, int dnsOffset, int qNameLen, bool isIpv4)
    {
        var totalLen = dnsOffset + 12 + qNameLen + 4;
        var resp = new byte[totalLen];

        Buffer.BlockCopy(req, 0, resp, 0, ipHdrLen);
        if (isIpv4)
        {
            Buffer.BlockCopy(req, 12, resp, 16, 4);
            Buffer.BlockCopy(req, 16, resp, 12, 4);
            BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(2, 2), (ushort)totalLen);
            resp[10] = 0;
            resp[11] = 0;
        }
        else
        {
            Buffer.BlockCopy(req, 8, resp, 24, 16);
            Buffer.BlockCopy(req, 24, resp, 8, 16);
            BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(4, 2), (ushort)(totalLen - 40));
        }

        Buffer.BlockCopy(req, udpOffset, resp, udpOffset + 2, 2);
        Buffer.BlockCopy(req, udpOffset + 2, resp, udpOffset, 2);
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(udpOffset + 4, 2), (ushort)(totalLen - udpOffset));
        resp[udpOffset + 6] = 0;
        resp[udpOffset + 7] = 0;

        Buffer.BlockCopy(req, dnsOffset, resp, dnsOffset, 12 + qNameLen + 4);
        var dnsSpan = resp.AsSpan(dnsOffset);
        BinaryPrimitives.WriteUInt16BigEndian(dnsSpan.Slice(2, 2), 0x8180);
        BinaryPrimitives.WriteUInt16BigEndian(dnsSpan.Slice(6, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(dnsSpan.Slice(8, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(dnsSpan.Slice(10, 2), 0);

        if (isIpv4)
        {
            uint sum = 0;
            for (var i = 0; i < ipHdrLen; i += 2)
            {
                sum += (uint)((resp[i] << 8) | resp[i + 1]);
            }
            while ((sum >> 16) != 0)
            {
                sum = (sum & 0xFFFF) + (sum >> 16);
            }
            sum = ~sum;
            resp[10] = (byte)(sum >> 8);
            resp[11] = (byte)(sum & 0xFF);
        }
        else
        {
            uint sum = 0;
            for (var i = 8; i < 40; i += 2)
            {
                sum += (uint)((resp[i] << 8) | resp[i + 1]);
            }
            var udpLen = totalLen - udpOffset;
            sum += (uint)udpLen;
            sum += 17;
            for (var i = 0; i < udpLen; i += 2)
            {
                if (i + 1 < udpLen)
                {
                    sum += (uint)((resp[udpOffset + i] << 8) + resp[udpOffset + i + 1]);
                }
                else
                {
                    sum += (uint)(resp[udpOffset + i] << 8);
                }
            }
            while ((sum >> 16) != 0)
            {
                sum = (sum & 0xFFFF) + (sum >> 16);
            }
            sum = ~sum;
            if (sum == 0)
            {
                sum = 0xFFFF;
            }
            resp[udpOffset + 6] = (byte)(sum >> 8);
            resp[udpOffset + 7] = (byte)(sum & 0xFF);
        }

        return resp;
    }
}
