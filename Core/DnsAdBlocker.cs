namespace obxodka.Core;

public static class DnsAdBlocker
{
    private static readonly HashSet<string> t_blockedDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "doubleclick.net",
        "googleadservices.com",
        "ads.google.com",
        "ad.doubleclick.net",
        "googlesyndication.com",
        "adservice.google.com",
        "pagead2.googlesyndication.com",
        "youtube.com/api/stats/ads",
        "yandex.ru/ads",
        "an.yandex.ru",
        "mc.yandex.ru",
        "appmetrica.yandex.ru",
        "vk.com/ads",
        "ads.vk.com",
        "ad.mail.ru",
        "r3.mail.ru",
        "rs.mail.ru",
        "top-fwz1.mail.ru",
        "amc.yandex.ru",
        "bs.yandex.ru",
        "banners.adfox.ru",
        "adfox.ru",
        "yadro.ru",
        "tns-counter.ru",
        "mc.yandex.md",
        "mc.yandex.by",
        "mc.yandex.kz",
        "metrics.apple.com",
        "ads.tiktok.com",
        "analytics.tiktok.com",
        "ads.yahoo.com",
        "analytics.twitter.com",
        "ads-twitter.com",
        "graph.facebook.com",
        "connect.facebook.net",
        "scorecardresearch.com",
        "admob.com",
        "unityads.unity3d.com",
        "applovin.com",
        "vungle.com"
    };
    public static byte[]? ProcessPacket(byte[] packet, int length)
    {
        if (!Preferences.Default.Get("use_adblock_dns", false))
        {
            return null;
        }
        try
        {
            if (length < 20)
            {
                return null;
            }
            var version = packet[0] >> 4;
            var ipHeaderLen = 0;
            var protocol = 0;

            if (version == 4)
            {
                ipHeaderLen = (packet[0] & 0x0F) * 4;
                if (length < ipHeaderLen + 8)
                {
                    return null;
                }
                protocol = packet[9];
            }
            else if (version == 6)
            {
                ipHeaderLen = 40;
                if (length < ipHeaderLen + 8)
                {
                    return null;
                }
                protocol = packet[6];
            }
            else
            {
                return null;
            }

            if (protocol != 17)
            {
                return null;
            }
            var udpHeaderOffset = ipHeaderLen;
            var srcPort = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(udpHeaderOffset, 2));
            var dstPort = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(udpHeaderOffset + 2, 2));

            if (dstPort != 53)
            {
                return null;
            }
            var dnsOffset = udpHeaderOffset + 8;
            if (length < dnsOffset + 12)
            {
                return null;
            }
            var dnsSpan = packet.AsSpan(dnsOffset, length - dnsOffset);
            var flags = BinaryPrimitives.ReadUInt16BigEndian(dnsSpan.Slice(2, 2));
            var qdCount = BinaryPrimitives.ReadUInt16BigEndian(dnsSpan.Slice(4, 2));

            if ((flags & 0x8000) != 0 || qdCount == 0)
            {
                return null;
            }
            var queryName = ParseDnsName(dnsSpan, 12, out var bytesRead);
            if (string.IsNullOrEmpty(queryName))
            {
                return null;
            }
            if (IsBlocked(queryName))
            {
                Debug.WriteLine($"[ADBLOCK] Blocked: {queryName}");
                return CreateDnsResponse(packet, ipHeaderLen, udpHeaderOffset, dnsOffset, bytesRead, version == 4);
            }
        }
        catch
        {

        }

        return null;
    }

    public static async Task ProcessLocalPacketAsync(byte[] packet, int length, Action<byte[]> sendBackToTun)
    {
        try
        {
            if (length < 20)
            {
                return;
            }
            var version = packet[0] >> 4;
            var ipHeaderLen = 0;
            var protocol = 0;
            var isIpv4 = version == 4;

            if (isIpv4)
            {
                ipHeaderLen = (packet[0] & 0x0F) * 4;
                if (length < ipHeaderLen + 8)
                {
                    return;
                }
                protocol = packet[9];
            }
            else if (version == 6)
            {
                ipHeaderLen = 40;
                if (length < ipHeaderLen + 8)
                {
                    return;
                }
                protocol = packet[6];
            }
            else
            {
                return;
            }
            if (protocol != 17)
            {
                return;
            }
            var udpOffset = ipHeaderLen;
            var dstPort = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(udpOffset + 2, 2));

            if (dstPort != 53)
            {
                return;
            }
            var dnsOffset = udpOffset + 8;
            if (length < dnsOffset + 12)
            {
                return;
            }
            var dnsSpan = packet.AsSpan(dnsOffset, length - dnsOffset);
            var flags = BinaryPrimitives.ReadUInt16BigEndian(dnsSpan.Slice(2, 2));
            var qdCount = BinaryPrimitives.ReadUInt16BigEndian(dnsSpan.Slice(4, 2));

            if ((flags & 0x8000) != 0 || qdCount == 0)
            {
                return;
            }
            var queryName = ParseDnsName(dnsSpan, 12, out var bytesRead);
            if (string.IsNullOrEmpty(queryName))
            {
                return;
            }
            if (IsBlocked(queryName))
            {
                Debug.WriteLine($"[ADBLOCK_LOCAL] Blocked: {queryName}");
                var resp = CreateDnsResponse(packet, ipHeaderLen, udpOffset, dnsOffset, bytesRead, isIpv4);
                sendBackToTun(resp);
                return;
            }

            var dnsPayload = new byte[length - dnsOffset];
            Buffer.BlockCopy(packet, dnsOffset, dnsPayload, 0, dnsPayload.Length);

            using var udp = new System.Net.Sockets.UdpClient();
            var sendTask = udp.SendAsync(dnsPayload, dnsPayload.Length, "1.1.1.1", 53);
            var delayTask = Task.Delay(2000);
            if (await Task.WhenAny(sendTask, delayTask) == delayTask)
            {
                return;
            }
            var recvTask = udp.ReceiveAsync();
            if (await Task.WhenAny(recvTask, Task.Delay(2000)) == recvTask)
            {
                var result = await recvTask;
                var resp = WrapUpstreamDnsPayload(packet, ipHeaderLen, udpOffset, result.Buffer, isIpv4);
                sendBackToTun(resp);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ADBLOCK_FWD] {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    private static byte[] WrapUpstreamDnsPayload(byte[] req, int ipHdrLen, int udpOffset, byte[] upstreamPayload, bool isIpv4)
    {
        var totalLen = udpOffset + 8 + upstreamPayload.Length;
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

        Buffer.BlockCopy(upstreamPayload, 0, resp, udpOffset + 8, upstreamPayload.Length);

        if (isIpv4)
        {
            uint sum = 0;
            for (var i = 0; i < ipHdrLen; i += 2)
            {
                sum += (uint)((resp[i] << 8) + resp[i + 1]);
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
                sum += (uint)((resp[i] << 8) + resp[i + 1]);
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

    private static bool IsBlocked(string domain)
    {
        if (t_blockedDomains.Contains(domain))
        {
            return true;
        }
        foreach (var blocked in t_blockedDomains)
        {
            if (domain.EndsWith("." + blocked, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ParseDnsName(ReadOnlySpan<byte> dnsSpan, int offset, out int bytesRead)
    {
        var sb = new StringBuilder();
        var currentOffset = offset;
        bytesRead = 0;

        while (currentOffset < dnsSpan.Length)
        {
            int len = dnsSpan[currentOffset++];
            if (len == 0)
            {
                break;
            }

            if ((len & 0xC0) == 0xC0)
            {

                currentOffset++;
                break;
            }

            if (sb.Length > 0)
            {
                _ = sb.Append('.');
            }
            if (currentOffset + len <= dnsSpan.Length)
            {
                _ = sb.Append(Encoding.UTF8.GetString(dnsSpan.Slice(currentOffset, len)));
            }
            currentOffset += len;
        }

        bytesRead = currentOffset - offset;
        return sb.ToString();
    }

    private static byte[] CreateDnsResponse(byte[] req, int ipHdrLen, int udpOffset, int dnsOffset, int qNameLen, bool isIpv4)
    {

        var totalLen = dnsOffset + 12 + qNameLen + 4 + 16;
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

        BinaryPrimitives.WriteUInt16BigEndian(dnsSpan.Slice(6, 2), 1);

        var ansOffset = dnsOffset + 12 + qNameLen + 4;
        var ansSpan = resp.AsSpan(ansOffset);

        BinaryPrimitives.WriteUInt16BigEndian(ansSpan[..2], 0xC00C);

        BinaryPrimitives.WriteUInt16BigEndian(ansSpan.Slice(2, 2), 1);

        BinaryPrimitives.WriteUInt16BigEndian(ansSpan.Slice(4, 2), 1);

        BinaryPrimitives.WriteUInt32BigEndian(ansSpan.Slice(6, 4), 300);

        BinaryPrimitives.WriteUInt16BigEndian(ansSpan.Slice(10, 2), 4);

        ansSpan[12] = 0;
        ansSpan[13] = 0;
        ansSpan[14] = 0;
        ansSpan[15] = 0;

        if (isIpv4)
        {

            uint sum = 0;
            for (var i = 0; i < ipHdrLen; i += 2)
            {
                sum += (uint)((resp[i] << 8) + resp[i + 1]);
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
                sum += (uint)((resp[i] << 8) + resp[i + 1]);
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
