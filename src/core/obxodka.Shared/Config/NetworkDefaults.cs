namespace obxodka.Shared.Config;

public static class NetworkDefaults
{
    public const int DefaultMtu = 1280;
    public const int MinMtu = 1280;
    public const int MaxMtu = 1420;

    public static readonly string[] TrustedDnsServers =
    [
        "1.1.1.1",
        "1.0.0.1",
        "8.8.8.8",
        "8.8.4.4",
        "9.9.9.9",
        "149.112.112.112",
        "77.88.8.8"
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
}
