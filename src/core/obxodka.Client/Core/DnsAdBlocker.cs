namespace obxodka.Core;

public static class DnsAdBlocker
{
    private sealed class RuleSet
    {
        public FrozenSet<string> Set { get; }
        public FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Lookup { get; }

        public RuleSet(IEnumerable<string> domains)
        {
            Set = domains.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
            Lookup = Set.GetAlternateLookup<ReadOnlySpan<char>>();
        }
    }

    private static readonly string[] t_defaultBlockedDomains =
    [
        "use-application-dns.net",
        "appmetrica.yandex.net",
        "appmetrica.yandex.ru",
        "metrika.yandex.ru",
        "metrika.yandex.com",
        "metrika.yandex.by",
        "metrika.yandex.kz",
        "metrika.yandex.uz",
        "mc.yandex.ru",
        "mc.yandex.com",
        "mc.yandex.md",
        "mc.yandex.kz",
        "extmaps-scans.yandex.net",
        "an.yandex.ru",
        "amc.yandex.ru",
        "bs.yandex.ru",
        "awaps.yandex.ru",
        "adfox.ru",
        "banners.adfox.ru",
        "ads.adfox.ru",
        "yabs.yandex.ru",
        "direct.yandex.ru",
        "analytics.mobile.yandex.net",
        "stat.vk.com",
        "top-fwz1.mail.ru",
        "r3.mail.ru",
        "rs.mail.ru",
        "ad.mail.ru",
        "ads.vk.com",
        "tns-counter.ru",
        "counter.yadro.ru",
        "yadro.ru",
        "target.my.com",
        "mytarget.com",
        "mstat.mail.ru",
        "hb.biz.mail.ru",
        "counter.rambler.ru",
        "ad.adriver.ru",
        "adriver.ru",
        "soloway.ru",
        "begun.ru",
        "relap.io",
        "analytics.sberbank.ru",
        "stat.sberbank.ru",
        "telemetry.sberbank.ru",
        "analytics.gosuslugi.ru",
        "stats.tinkoff.ru",
        "telemetry.tbank.ru",
        "stat.vtb.ru",
        "metrics.rutube.ru",
        "appsflyer.com",
        "app.appsflyer.com",
        "events.appsflyer.com",
        "t.appsflyer.com",
        "adjust.com",
        "app.adjust.com",
        "branch.io",
        "api2.branch.io",
        "kochava.com",
        "singular.net",
        "amplitude.com",
        "api.amplitude.com",
        "mixpanel.com",
        "api.mixpanel.com",
        "segment.io",
        "api.segment.io",
        "flurry.com",
        "data.flurry.com",
        "firebase-settings.crashlytics.com",
        "firebaselogging.googleapis.com",
        "app-measurement.com",
        "doubleclick.net",
        "ad.doubleclick.net",
        "googleadservices.com",
        "ads.google.com",
        "googlesyndication.com",
        "adservice.google.com",
        "pagead2.googlesyndication.com",
        "google-analytics.com",
        "ssl.google-analytics.com",
        "metrics.apple.com",
        "ads.tiktok.com",
        "analytics.tiktok.com",
        "ads.yahoo.com",
        "analytics.twitter.com",
        "ads-twitter.com",
        "graph.facebook.com",
        "connect.facebook.net",
        "pixel.facebook.com",
        "scorecardresearch.com",
        "admob.com",
        "unityads.unity3d.com",
        "unityads.com",
        "applovin.com",
        "telemetry.applovin.com",
        "vungle.com",
        "ads.vungle.com",
        "inmobi.com",
        "ironsource.com",
        "ironsrc.com",
        "supersonicads.com",
        "mintegral.com",
        "chartboost.com",
        "tapjoy.com",
        "adcolony.com",
        "fyber.com",
        "pangle.io",
        "ads.pangle.io",
        "mobvista.com",
        "smaato.net",
        "liftoff.io",
        "outbrain.com",
        "taboola.com",
        "criteo.com",
        "criteo.net",
        "rubiconproject.com",
        "pubmatic.com",
        "openx.net",
        "smartadserver.com",
        "adnxs.com",
        "popads.net",
        "propellerads.com",
        "exoclick.com",
        "mgid.com",
        "adkeeper.co.uk",
        "tracking.miui.com",
        "data.mistat.xiaomi.com",
        "metrics.data.hicloud.com",
        "logservice.hicloud.com",
        "samsungadhub.com",
        "smetrics.samsung.com",
        "ad.lgsmartad.com"
    ];

    private static readonly string[] t_defaultWhitelistDomains =
    [
        "clck.yandex.ru"
    ];

    private static volatile RuleSet t_rules = new(t_defaultBlockedDomains);
    private static volatile RuleSet t_whitelist = new(t_defaultWhitelistDomains);
    private static volatile bool t_isAdBlockEnabled = true;
    private static long t_blockedQueriesCount;

    public static bool IsAdBlockEnabled
    {
        get => t_isAdBlockEnabled;
        set => t_isAdBlockEnabled = value;
    }

    public static long BlockedQueriesCount => Volatile.Read(ref t_blockedQueriesCount);

    public static void ResetBlockedCount() => Interlocked.Exchange(ref t_blockedQueriesCount, 0);

    public static int BlockedDomainsCount => t_rules.Set.Count;

    public static int WhitelistDomainsCount => t_whitelist.Set.Count;

    public static void ResetToDefaults()
    {
        t_rules = new RuleSet(t_defaultBlockedDomains);
        t_whitelist = new RuleSet(t_defaultWhitelistDomains);
        ResetBlockedCount();
    }

    public static void UpdateBlockedDomains(IEnumerable<string> domains, bool mergeWithDefaults = false)
    {
        if (mergeWithDefaults)
        {
            var merged = new HashSet<string>(t_defaultBlockedDomains, StringComparer.OrdinalIgnoreCase);
            merged.UnionWith(domains);
            t_rules = new RuleSet(merged);
        }
        else
        {
            t_rules = new RuleSet(domains);
        }
    }

    public static void UpdateWhitelist(IEnumerable<string> domains) =>
        t_whitelist = new RuleSet(domains);

    public static void AddToWhitelist(string domain)
    {
        var current = new HashSet<string>(t_whitelist.Set, StringComparer.OrdinalIgnoreCase) { domain };
        t_whitelist = new RuleSet(current);
    }

    public static void RemoveFromWhitelist(string domain)
    {
        var current = new HashSet<string>(t_whitelist.Set, StringComparer.OrdinalIgnoreCase);
        _ = current.Remove(domain);
        t_whitelist = new RuleSet(current);
    }

    public static bool IsWhitelisted(ReadOnlySpan<char> domain) =>
        t_whitelist.Lookup.Contains(domain);

    public static bool IsBlocked(ReadOnlySpan<char> domain)
    {
        if (IsWhitelisted(domain))
        {
            return false;
        }

        var rules = t_rules;
        if (rules.Lookup.Contains(domain))
        {
            return true;
        }

        var remaining = domain;
        while (true)
        {
            var dot = remaining.IndexOf('.');
            if (dot < 0 || dot + 1 >= remaining.Length)
            {
                break;
            }
            remaining = remaining[(dot + 1)..];
            if (rules.Lookup.Contains(remaining))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsBlocked(string domain) => IsBlocked(domain.AsSpan());

    public static byte[]? ProcessPacket(byte[] packet, int length, bool? useAdblock = null)
    {
        var enabled = useAdblock ?? IsAdBlockEnabled;
        if (!enabled || length < 20)
        {
            return null;
        }

        try
        {
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

            Span<char> domainBuf = stackalloc char[256];
            if (!TryParseDnsName(dnsSpan, 12, domainBuf, out var charsWritten, out var bytesRead))
            {
                return null;
            }

            var domainSpan = domainBuf[..charsWritten];
            if (IsBlocked(domainSpan))
            {
                _ = Interlocked.Increment(ref t_blockedQueriesCount);
                var isCanary = domainSpan.SequenceEqual("use-application-dns.net");
                return CreateDnsResponse(packet, ipHeaderLen, udpHeaderOffset, dnsOffset, bytesRead, version == 4, isCanary);
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
            if (!IsAdBlockEnabled || length < 20)
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

            Span<char> domainBuf = stackalloc char[256];
            if (!TryParseDnsName(dnsSpan, 12, domainBuf, out var charsWritten, out var bytesRead))
            {
                return;
            }

            var domainSpan = domainBuf[..charsWritten];
            if (IsBlocked(domainSpan))
            {
                _ = Interlocked.Increment(ref t_blockedQueriesCount);
                var isCanary = domainSpan.SequenceEqual("use-application-dns.net");
                var resp = CreateDnsResponse(packet, ipHeaderLen, udpOffset, dnsOffset, bytesRead, isIpv4, isCanary);
                sendBackToTun(resp);
                return;
            }

            var dnsPayload = new byte[length - dnsOffset];
            Buffer.BlockCopy(packet, dnsOffset, dnsPayload, 0, dnsPayload.Length);

            using var udp = new UdpClient();
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

        ComputeChecksums(resp, ipHdrLen, udpOffset, totalLen, isIpv4);
        return resp;
    }

    private static bool TryParseDnsName(ReadOnlySpan<byte> dnsSpan, int offset, Span<char> dest, out int charsWritten, out int bytesRead)
    {
        charsWritten = 0;
        bytesRead = 0;
        var currentOffset = offset;

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

            if (charsWritten > 0)
            {
                if (charsWritten >= dest.Length)
                {
                    return false;
                }
                dest[charsWritten++] = '.';
            }

            if (currentOffset + len > dnsSpan.Length || charsWritten + len > dest.Length)
            {
                return false;
            }

            for (var i = 0; i < len; i++)
            {
                var b = dnsSpan[currentOffset + i];
                dest[charsWritten++] = (char)(b is >= (byte)'A' and <= (byte)'Z' ? b + 32 : b);
            }

            currentOffset += len;
        }

        bytesRead = currentOffset - offset;
        return charsWritten > 0;
    }

    public static string ParseDnsName(ReadOnlySpan<byte> dnsSpan, int offset, out int bytesRead)
    {
        Span<char> dest = stackalloc char[256];
        if (TryParseDnsName(dnsSpan, offset, dest, out var charsWritten, out bytesRead))
        {
            return new string(dest[..charsWritten]);
        }

        bytesRead = 0;
        return string.Empty;
    }

    private static byte[] CreateDnsResponse(byte[] req, int ipHdrLen, int udpOffset, int dnsOffset, int qNameLen, bool isIpv4, bool isNxDomain = false)
    {
        var qType = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(dnsOffset + 12 + qNameLen, 2));
        var isAaaa = qType == 28;
        var ansLen = (isAaaa || isNxDomain) ? 0 : 16;
        var totalLen = dnsOffset + 12 + qNameLen + 4 + ansLen;
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

        var flags = isNxDomain ? (ushort)0x8183 : (ushort)0x8180;
        BinaryPrimitives.WriteUInt16BigEndian(dnsSpan.Slice(2, 2), flags);
        BinaryPrimitives.WriteUInt16BigEndian(dnsSpan.Slice(6, 2), (isAaaa || isNxDomain) ? (ushort)0 : (ushort)1);

        if (!isAaaa && !isNxDomain)
        {
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
        }

        ComputeChecksums(resp, ipHdrLen, udpOffset, totalLen, isIpv4);
        return resp;
    }

    private static void ComputeChecksums(byte[] resp, int ipHdrLen, int udpOffset, int totalLen, bool isIpv4)
    {
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
    }
}
