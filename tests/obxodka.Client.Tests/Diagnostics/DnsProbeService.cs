namespace obxodka.Client.Diagnostics;

//проверка доступности dns серверов через udp запросы
public static class DnsProbeService
{
    private static readonly byte[] s_dnsQuery =
    [
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
    ];

    //тестирование списка dns серверов с возвратом ответивших с наименьшим пингом
    public static async Task<string[]> GetHealthyDnsServersAsync(
        string[]? dnsServers = null,
        int timeoutMs = 600,
        CancellationToken ct = default)
    {
        var targets = dnsServers ?? DnsDefaults.TrustedDnsServers;
        var tasks = new List<Task<(string ip, long rttMs, bool ok)>>();

        foreach (var ip in targets)
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

            return healthy.Length > 0 ? healthy : targets;
        }
        catch
        {
            return targets;
        }
    }

    //отправка пробного dns запроса на конкретный ip
    private static async Task<(string ip, long rttMs, bool ok)> ProbeDnsServerAsync(string ip, int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var ep = new IPEndPoint(IPAddress.Parse(ip), 53);
            _ = await udp.SendAsync(s_dnsQuery, s_dnsQuery.Length, ep).WaitAsync(cts.Token).ConfigureAwait(false);
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
