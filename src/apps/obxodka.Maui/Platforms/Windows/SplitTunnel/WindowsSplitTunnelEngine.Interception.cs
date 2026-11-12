using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using obxodka.Shared.Geo;

namespace obxodka.Platforms.Windows;

//перехват и анализ исходящих и входящих пакетов wintun для раздельного туннелирования
internal static partial class WindowsSplitTunnelEngine
{
    //анализ входящих пакетов из vpn для извлечения dns-ответов и закрепления маршрутов
    public static void InspectIncomingPacketFromVpn(ReadOnlySpan<byte> packet, int length)
    {
        if (length < 40 || s_physicalGatewayUint == 0)
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

    //перехват исходящих пакетов wintun для dns snoop, sni и проверки процессов
    public static bool TryInterceptOutgoingPacket(
        byte[] packetBuffer,
        int length,
        WintunAdapter adapter)
    {
        if (length < 28 || s_physicalGatewayUint == 0)
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
            else if (nextHdr == 6 && hasApps && length >= 60)
            {
                var tcpFlags = packet[53];
                var isSyn = (tcpFlags & 0x02) != 0 && (tcpFlags & 0x10) == 0;
                if (isSyn && IsPortOwnedByBypassedProcess(true, srcPort6, out _, isIpv6: true))
                {
                    SendIpv6TcpReset(packet, adapter);
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
        var dstIpUint = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(16, 4));

        if (s_pinnedRoutes.ContainsKey(dstIpUint))
        {
            return false;
        }

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
            else
            {
                if (policy.BypassAllRuTlds && RuSubnetRegistry.IsRuIpv4(packet.Slice(16, 4)))
                {
                    PinPhysicalHostRoute(dstIpUint, "Auto-Bypass RU (IP Range)");
                    return true;
                }

                if (hasApps)
                {
                    var flowKey = ((ulong)dstIpUint << 32) | ((uint)srcPort << 16) | dstPort;
                    if (!s_checkedUdpFlows.ContainsKey(flowKey))
                    {
                        s_checkedUdpFlows[flowKey] = 1;
                        if (IsPortOwnedByBypassedProcess(false, srcPort, out var exeName))
                        {
                            PinPhysicalHostRoute(dstIpUint, exeName);
                            return true;
                        }
                    }
                }
            }
        }
        else if (protocol == 6)
        {
            if (length < ihl + 20)
            {
                return false;
            }

            var tcpOffset = ihl;
            var srcPort = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(tcpOffset, 2));
            var dstPort = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(tcpOffset + 2, 2));
            var tcpHdrLen = ((packet[tcpOffset + 12] >> 4) & 0x0F) * 4;
            var tcpFlags = packet[tcpOffset + 13];
            var isSyn = (tcpFlags & 0x02) != 0 && (tcpFlags & 0x10) == 0;

            if (isSyn)
            {
                if (policy.BypassAllRuTlds && RuSubnetRegistry.IsRuIpv4(packet.Slice(16, 4)))
                {
                    PinPhysicalHostRoute(dstIpUint, "Auto-Bypass RU (IP Range)");
                    SendTcpReset(packet, ihl, adapter);
                    return true;
                }

                if (hasApps && IsPortOwnedByBypassedProcess(true, srcPort, out var exeName))
                {
                    PinPhysicalHostRoute(dstIpUint, exeName);
                    SendTcpReset(packet, ihl, adapter);
                    return true;
                }
            }

            var payloadOffset = tcpOffset + tcpHdrLen;
            if (length > payloadOffset)
            {
                var payload = packet[payloadOffset..];

                if (payload.Length > 5)
                {
                    if (hasDomains)
                    {
                        var detectedHost = string.Empty;
                        if (dstPort == 443 && payload[0] == 0x16 && payload[1] == 0x03)
                        {
                            _ = TryExtractTlsSni(payload, out detectedHost);
                        }
                        else if (dstPort == 80 && (payload[0] == (byte)'G' || payload[0] == (byte)'P' || payload[0] == (byte)'H'))
                        {
                            _ = TryExtractHttpHost(payload, out detectedHost);
                        }

                        if (!string.IsNullOrEmpty(detectedHost) && policy.IsDomainBypassed(detectedHost))
                        {
                            PinPhysicalHostRoute(dstIpUint, detectedHost);
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

    //пересылка исключенного dns-запроса через физический сетевой адаптер
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

    //выполнение сырого udp dns запроса через сокет физической сетевой карты
    private static async Task<byte[]?> QueryPhysicalDnsRawAsync(byte[] dnsPayload)
    {
        Span<byte> gwBytes = stackalloc byte[4];
        var gwUint = s_physicalGatewayUint;
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
                if (s_physicalLocalIp is not null && !IPAddress.IsLoopback(s_physicalLocalIp))
                {
                    try
                    {
                        socket.Bind(new IPEndPoint(s_physicalLocalIp, 0));
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

    //генерация бинарного полезного содержимого dns-запроса типа a
    private static byte[] BuildDnsAQueryPayload(string domain)
    {
        var labels = domain.Trim('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        using var ms = new MemoryStream(64);
        Span<byte> hdr = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(hdr[..2], (ushort)RandomNumberGenerator.GetInt32(1, 65535));
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

    //предварительный физический dns-резолв посевных доменов
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
}
