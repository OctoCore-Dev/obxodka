using obxodka.Models;

namespace obxodka.Shared.Config;

public static class NetworkDefaults
{
    public const int DefaultMtu = 1360;
    public const int MinMtu = 1280;
    public const int MaxMtu = 1420;
    public static int CurrentMtu { get; set; } = DefaultMtu;
    public static int[] MtuCandidates => GenerateMtuScanCandidates(MaxMtu, MinMtu, 8);

    public static int[] GenerateMtuScanCandidates(int max = MaxMtu, int min = MinMtu, int step = 8)
    {
        var candidates = new List<int>();
        for (var m = max; m >= min; m -= step)
        {
            candidates.Add(m);
        }
        if (candidates.Count == 0 || candidates[^1] != min)
        {
            candidates.Add(min);
        }
        return [.. candidates];
    }

    public static readonly string[] TrustedDnsServers =
    [
        "77.88.8.8",
        "77.88.8.1",
        "1.1.1.1",
        "1.0.0.1",
        "8.8.8.8",
        "8.8.4.4",
        "9.9.9.9",
        "149.112.112.112",
        "208.67.222.222",
        "208.67.220.220"
    ];

    public static readonly string PrimaryDns = "1.1.1.1";
    public static readonly string SecondaryDns = "1.0.0.1";

    public static int ClampMtu(int mtu) => Math.Clamp(mtu, MinMtu, MaxMtu);

    public static async Task<string[]> GetHealthyDnsServersAsync(int timeoutMs = 600, CancellationToken ct = default)
    {
        var tasks = new List<Task<(string ip, long rttMs, bool ok)>>();

        foreach (var ip in TrustedDnsServers)
        {
            tasks.Add(ProbeDnsServerAsync(ip, timeoutMs, ct));
        }

        try
        {
            var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs + 200), ct).ConfigureAwait(false);
            var healthy = results
                .Where(r => r.ok)
                .OrderBy(r => r.rttMs)
                .Select(r => r.ip)
                .ToArray();

            return healthy.Length > 0 ? healthy : TrustedDnsServers;
        }
        catch
        {
            return TrustedDnsServers;
        }
    }

    private static async Task<(string ip, long rttMs, bool ok)> ProbeDnsServerAsync(string ip, int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var query = new byte[]
            {
                0xAA, 0xBB,
                0x01, 0x00,
                0x00, 0x01,
                0x00, 0x00,
                0x00, 0x00,
                0x00, 0x00,
                0x03, 0x77, 0x77, 0x77,
                0x06, 0x67, 0x6F, 0x6F, 0x67, 0x6C, 0x65,
                0x03, 0x63, 0x6F, 0x6D,
                0x00,
                0x00, 0x01,
                0x00, 0x01
            };

            var ep = new IPEndPoint(IPAddress.Parse(ip), 53);
            _ = await udp.SendAsync(query, query.Length, ep).WaitAsync(cts.Token).ConfigureAwait(false);
            _ = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
            sw.Stop();
            return (ip, sw.ElapsedMilliseconds, true);
        }
        catch
        {
            return (ip, long.MaxValue, false);
        }
    }

    public static async Task<IReadOnlyList<VpnServerDto>> RankServersByLatencyAsync(
        IReadOnlyList<VpnServerDto> servers,
        int timeoutMs = 800,
        CancellationToken ct = default)
    {
        if (servers is null || servers.Count <= 1)
        {
            return servers ?? [];
        }

        var tasks = new List<Task<(VpnServerDto server, long rttMs, bool ok)>>();
        foreach (var s in servers)
        {
            tasks.Add(ProbeServerAsync(s, timeoutMs, ct));
        }

        try
        {
            var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs + 200), ct).ConfigureAwait(false);
            var healthy = results
                .Where(r => r.ok)
                .OrderBy(r => r.rttMs + (r.server.LoadPercent / 5))
                .Select(r => r.server)
                .ToList();

            var unreachable = results
                .Where(r => !r.ok)
                .Select(r => r.server);

            healthy.AddRange(unreachable);
            return healthy.Count > 0 ? healthy : servers;
        }
        catch
        {
            return servers;
        }
    }

    private static async Task<(VpnServerDto server, long rttMs, bool ok)> ProbeServerAsync(
        VpnServerDto server,
        int timeoutMs,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            var targetIp = server.Ip;
            if (Uri.CheckHostName(targetIp) == UriHostNameType.Dns)
            {
                var addrs = await Dns.GetHostAddressesAsync(targetIp, cts.Token).ConfigureAwait(false);
                var ipv4 = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ipv4 is null)
                {
                    return (server, long.MaxValue, false);
                }
                targetIp = ipv4.ToString();
            }

            var port = server.Port > 0 ? server.Port : 443;
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(targetIp), port), cts.Token).ConfigureAwait(false);
            sw.Stop();
            return (server, sw.ElapsedMilliseconds, true);
        }
        catch
        {
            return (server, long.MaxValue, false);
        }
    }

    public static string? FlagEmojiToCountryCode(string? emoji)
    {
        if (string.IsNullOrWhiteSpace(emoji) || emoji.Length < 4)
        {
            return null;
        }

        var cp1 = char.ConvertToUtf32(emoji, 0);
        var cp2 = char.ConvertToUtf32(emoji, 2);

        if (cp1 >= 0x1F1E6 && cp1 <= 0x1F1FF && cp2 >= 0x1F1E6 && cp2 <= 0x1F1FF)
        {
            var c1 = (char)('a' + (cp1 - 0x1F1E6));
            var c2 = (char)('a' + (cp2 - 0x1F1E6));
            return $"{c1}{c2}";
        }

        return null;
    }

    public static string? GetFlagImageUrl(string? emoji)
    {
        var code = FlagEmojiToCountryCode(emoji);
        return !string.IsNullOrEmpty(code)
            ? $"https://flagcdn.com/w80/{code}.png"
            : null;
    }
}
