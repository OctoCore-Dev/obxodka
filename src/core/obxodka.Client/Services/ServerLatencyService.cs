namespace obxodka.Services;

//проверка задержки и сортировка серверов по пингу и нагрузке
public static class ServerLatencyService
{
    //параллельный замер задержки серверов
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

    //замер пинга одного сервера по tcp
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
}
