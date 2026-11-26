namespace obxodka.Core;

//сканер чистых anycast-подсетей cdn с проверкой реальной прокачки >20 кб против заморозки тспу на 16 кб
internal static class CleanEdgeScanner
{
    //минимальный порог скачивания в байтах для подтверждения отсутствия заморозки тспу на 16 кб
    public const int AntiFreezeMinBytes = 20480;

    //проверенные разнообразные /24 префиксы cloudflare anycast (исключая проблемный пул 188.114.96.0/20)
    private static readonly (byte A, byte B, byte C, byte AnchorHost)[] s_verifiedAnycastPrefixes =
    [
        (104, 18, 32, 7),
        (162, 159, 135, 42),
        (172, 64, 155, 2),
        (198, 41, 200, 53),
        (104, 16, 132, 229),
        (104, 17, 64, 12),
        (104, 19, 192, 29),
        (108, 162, 196, 44),
        (141, 101, 120, 81),
        (162, 158, 15, 90),
        (172, 66, 40, 118),
        (173, 245, 58, 101),
        (190, 93, 244, 19),
        (198, 41, 192, 67)
    ];

    //кэш проверенных чистых edge ip по ключу (host:port)
    private static readonly ConcurrentDictionary<string, (IPAddress[] Ips, long ExpiresAtTicks)> s_cleanCache =
        new(StringComparer.OrdinalIgnoreCase);

    //защита от параллельного дублирования сканирования одного и того же хоста
    private static readonly SemaphoreSlim s_scanGate = new(1, 1);

    private static int s_prefixCursor;

    //сброс кэша найденных чистых адресов при смене физической сети
    public static void InvalidateCache() => s_cleanCache.Clear();

    //проверка принадлежит ли адрес деградировавшему в рф пулу 188.114.96.0/20
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsThrottledDefaultEdgeIp(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[4];
        return ip.TryWriteBytes(bytes, out _) &&
               bytes[0] == 188 &&
               bytes[1] == 114 &&
               bytes[2] is >= 96 and <= 111;
    }

    //формирование приоритетного списка адресов для облачного транспорта (живые anycast >20 кб + здоровые dns ip)
    public static async ValueTask<IPAddress[]> ResolveCleanShieldCandidatesAsync(
        string targetSni,
        int port,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetSni))
        {
            return [];
        }

        if (IPAddress.TryParse(targetSni, out var literalIp))
        {
            return [literalIp];
        }

        if (targetSni.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return [IPAddress.Loopback];
        }

        var cacheKey = $"{targetSni}:{port}";
        var nowTicks = Stopwatch.GetTimestamp();
        if (s_cleanCache.TryGetValue(cacheKey, out var cached) && nowTicks < cached.ExpiresAtTicks)
        {
            var healthyCached = FilterHealthy(cached.Ips);
            if (healthyCached.Length > 0)
            {
                return healthyCached;
            }
        }

        IPAddress[] dnsIps = [];
        try
        {
            dnsIps = await ApiService.ResolveAllHostsAntiPoisonAsync(targetSni, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }
        }

        var scanned = await ScanCleanAnycastPoolAsync(targetSni, port, ct).ConfigureAwait(false);
        var merged = MergeCandidates(scanned, dnsIps);

        if (merged.Length > 0)
        {
            s_cleanCache[cacheKey] = (merged, Stopwatch.GetTimestamp() + Stopwatch.Frequency * 600);
            await OctopusEngine.Current.RegisterExternalEdgeAddressesAsync(merged, ct).ConfigureAwait(false);
        }

        return merged;
    }

    //генерация пачки кандидатов из разных /24 префиксов для параллельной проверки
    internal static IPAddress[] GenerateProbeBatch(int count = 6)
    {
        var batchLen = Math.Clamp(count, 1, s_verifiedAnycastPrefixes.Length);
        var result = new IPAddress[batchLen];
        var start = Math.Abs(Interlocked.Add(ref s_prefixCursor, batchLen)) % s_verifiedAnycastPrefixes.Length;
        Span<byte> addrBytes = stackalloc byte[4];

        for (var i = 0; i < batchLen; i++)
        {
            var (a, b, c, anchorHost) = s_verifiedAnycastPrefixes[(start + i) % s_verifiedAnycastPrefixes.Length];
            addrBytes[0] = a;
            addrBytes[1] = b;
            addrBytes[2] = c;
            //первая половина батча берет эталонные якорные узлы, вторая — случайный хост внутри /24
            addrBytes[3] = (i & 1) == 0
                ? anchorHost
                : (byte)RandomNumberGenerator.GetInt32(2, 250);
            result[i] = new IPAddress(addrBytes);
        }

        return result;
    }

    //параллельное зондирование пула anycast-подсетей с отсевом заморозки 16 кб
    private static async Task<IPAddress[]> ScanCleanAnycastPoolAsync(string targetSni, int port, CancellationToken ct)
    {
        var entered = await s_scanGate.WaitAsync(TimeSpan.FromMilliseconds(1800), ct).ConfigureAwait(false);
        if (!entered)
        {
            return [];
        }

        try
        {
            var cacheKey = $"{targetSni}:{port}";
            if (s_cleanCache.TryGetValue(cacheKey, out var cached) && Stopwatch.GetTimestamp() < cached.ExpiresAtTicks)
            {
                var healthyCached = FilterHealthy(cached.Ips);
                if (healthyCached.Length > 0)
                {
                    return healthyCached;
                }
            }

            var batch = GenerateProbeBatch(6);
            using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            scanCts.CancelAfter(TimeSpan.FromMilliseconds(1750));

            var results = new ConcurrentBag<(IPAddress Ip, long ElapsedMs, bool Full20KbVerified)>();
            var pending = new List<Task>(batch.Length);

            foreach (var candidateIp in batch)
            {
                if (EdgeEndpointHealth.IsUnhealthy(candidateIp.ToString()))
                {
                    continue;
                }

                pending.Add(Task.Run(async () =>
                {
                    var (success, fullThroughputVerified, elapsedMs) =
                        await ProbeSingleEdgeAsync(candidateIp, port, targetSni, scanCts.Token).ConfigureAwait(false);
                    if (success)
                    {
                        results.Add((candidateIp, elapsedMs, fullThroughputVerified));
                        if (results.Count(static r => r.Full20KbVerified) >= 2)
                        {
                            try
                            {
                                await scanCts.CancelAsync().ConfigureAwait(false);
                            }
                            catch (ObjectDisposedException)
                            {
                                //токен сканирования уже освобожден после завершения гонки
                            }
                        }
                    }
                }, CancellationToken.None));
            }

            try
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            catch
            {
                //отдельные таймауты кандидатов ожидаемы при параллельной гонке
            }

            var ordered = results
                .OrderByDescending(static r => r.Full20KbVerified)
                .ThenBy(static r => r.ElapsedMs)
                .Select(static r => r.Ip)
                .ToArray();

            if (ordered.Length > 0)
            {
                AppLogger.Log(
                    $"[CLEAN-EDGE] Found {ordered.Length} clean CDN edge IPs for '{targetSni}:{port}' (best={ordered[0]}, >20KB={results.Any(static r => r.Full20KbVerified)})");
            }

            return ordered;
        }
        finally
        {
            _ = s_scanGate.Release();
        }
    }

    //реальная проверка конкретного edge ip: tcp + tls + скачивание >20 кб статики из кэша cdn
    private static async Task<(bool Success, bool FullThroughputVerified, long ElapsedMs)> ProbeSingleEdgeAsync(
        IPAddress candidateIp,
        int port,
        string targetSni,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var socket = new Socket(candidateIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        OctopusEngine.Current.ProtectSocket(socket);

        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromMilliseconds(900));
            await socket.ConnectAsync(new IPEndPoint(candidateIp, port), connectCts.Token).ConfigureAwait(false);

            using var netStream = new NetworkStream(socket, false);
            using var dpiStream = DpiBypassStream.CreateForProfile(netStream, TlsBrowserProfile.Chrome, true, true);
            using var sslStream = new SslStream(dpiStream, false);

            var sslOptions = SslOptionsFactory.Create(
                targetSni,
                string.Empty,
                [SslApplicationProtocol.Http11]);

            await sslStream.AuthenticateAsClientAsync(sslOptions, ct).ConfigureAwait(false);

            var reqText =
                $"GET /css/tailwind.css HTTP/1.1\r\nHost: {targetSni}\r\nUser-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36\r\nRange: bytes=0-24575\r\nAccept: */*\r\nConnection: close\r\n\r\n";
            var reqBytes = Encoding.ASCII.GetBytes(reqText);
            await sslStream.WriteAsync(reqBytes, ct).ConfigureAwait(false);
            await sslStream.FlushAsync(ct).ConfigureAwait(false);

            var rxBuf = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                var totalRead = 0;
                var headerChecked = false;
                var statusOk = false;

                while (totalRead < AntiFreezeMinBytes + 512)
                {
                    var n = await sslStream.ReadAsync(rxBuf, ct).ConfigureAwait(false);
                    if (n <= 0)
                    {
                        break;
                    }

                    if (!headerChecked)
                    {
                        headerChecked = true;
                        var previewLen = Math.Min(n, 64);
                        var statusLine = Encoding.ASCII.GetString(rxBuf, 0, previewLen);
                        statusOk = statusLine.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase) ||
                                   statusLine.StartsWith("HTTP/1.1 206", StringComparison.OrdinalIgnoreCase) ||
                                   statusLine.StartsWith("HTTP/1.1 301", StringComparison.OrdinalIgnoreCase) ||
                                   statusLine.StartsWith("HTTP/1.1 302", StringComparison.OrdinalIgnoreCase) ||
                                   statusLine.StartsWith("HTTP/1.1 404", StringComparison.OrdinalIgnoreCase);
                        if (!statusOk)
                        {
                            return (false, false, sw.ElapsedMilliseconds);
                        }
                    }

                    totalRead += n;
                    if (totalRead >= AntiFreezeMinBytes)
                    {
                        sw.Stop();
                        return (true, true, sw.ElapsedMilliseconds);
                    }
                }

                sw.Stop();
                return statusOk && totalRead >= 128
                    ? (true, false, sw.ElapsedMilliseconds)
                    : (false, false, sw.ElapsedMilliseconds);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rxBuf);
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException or OperationCanceledException
                                       or ObjectDisposedException)
        {
            return (false, false, sw.ElapsedMilliseconds);
        }
    }

    private static IPAddress[] FilterHealthy(IPAddress[] addresses) =>
        [.. addresses.Where(static a => !EdgeEndpointHealth.IsUnhealthy(a.ToString()))];

    private static IPAddress[] MergeCandidates(IPAddress[] scannedClean, IPAddress[] dnsIps)
    {
        List<IPAddress> primary = [with(scannedClean.Length + dnsIps.Length)];
        List<IPAddress> throttledFallback = [];
        var seen = new HashSet<IPAddress>();

        foreach (var ip in scannedClean.Where(ip => !EdgeEndpointHealth.IsUnhealthy(ip.ToString()) && seen.Add(ip)))
        {
            primary.Add(ip);
        }

        foreach (var ip in EdgeEndpointHealth.FilterAndRotate(dnsIps))
        {
            if (!seen.Add(ip))
            {
                continue;
            }

            if (IsThrottledDefaultEdgeIp(ip) && primary.Count > 0)
            {
                throttledFallback.Add(ip);
            }
            else
            {
                primary.Add(ip);
            }
        }

        primary.AddRange(throttledFallback);
        return [.. primary];
    }
}
