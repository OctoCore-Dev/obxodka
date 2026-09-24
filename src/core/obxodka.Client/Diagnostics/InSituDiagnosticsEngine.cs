using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace obxodka.Client.Diagnostics;

public sealed record RstFingerprint(
    int ObservedTtl,
    ushort IpId,
    uint WindowSize,
    bool IsInjectedByTspu,
    string Details);

public static class InSituDiagnosticsEngine
{
    public static async Task<(int hopDistance, string details)> MapTspuHopDistanceAsync(
        string host,
        int port,
        int maxHops = 12,
        CancellationToken ct = default)
    {
        for (var ttl = 1; ttl <= maxHops; ttl++)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
                ReceiveTimeout = 300,
                SendTimeout = 300
            };

            try
            {
                socket.Ttl = (short)ttl;
            }
            catch
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, ttl);
                }
                catch { }
            }

            var sw = Stopwatch.StartNew();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(350);

            try
            {
                await socket.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
                sw.Stop();
                return (ttl, $"Целевой хост ответил на хопе {ttl} (RTT: {sw.ElapsedMilliseconds} ms, чистый маршрут)");
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset || ex.ErrorCode == 10054)
            {
                sw.Stop();
                return (ttl, $"Обнаружен инжектированный TCP RST на хопе {ttl} (RTT: {sw.ElapsedMilliseconds} ms) -> Точная дистанция до ТСПУ: {ttl} хопов");
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable)
            {
            }
            catch
            {
            }
        }

        return (-1, "ТСПУ не сбросил соединение в пределах первых хопов (маршрут свободен или сброс происходит после TLS Handshake)");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RstFingerprint AnalyzeRstPacket(int observedTtl, ushort ipId = 0, uint windowSize = 0)
    {
        var isInjected = false;
        var reasons = new List<string>();

        if (observedTtl is 64 or 128 or 255)
        {
            isInjected = true;
            reasons.Add($"Характерный дефолтный TTL инжектора ({observedTtl})");
        }

        if (ipId == 0)
        {
            isInjected = true;
            reasons.Add("Обнуленный IP ID (паттерн EcoFilter/ТСПУ)");
        }
        else if (ipId == 0x4242)
        {
            isInjected = true;
            reasons.Add("Специфический маркер генератора RST (0x4242)");
        }

        if (windowSize == 0)
        {
            reasons.Add("Нулевой размер TCP Window");
        }

        var details = isInjected
            ? $"АНОМАЛИЯ ТСПУ ПОДТВЕРЖДЕНА: {string.Join(", ", reasons)}"
            : "Стандартный TCP RST удаленного узла (без признаков подделки)";

        return new RstFingerprint(observedTtl, ipId, windowSize, isInjected, details);
    }

    public static async Task<(bool allPassed, string details)> TestCanaryEchoAsync(
        HttpClient httpClient,
        CancellationToken ct = default)
    {
        var targets = new (string Name, string Url)[]
        {
            ("Cloudflare (1.1.1.1)", "https://1.1.1.1/dns-query?name=google.com&type=A"),
            ("Google (8.8.8.8)", "https://dns.google/resolve?name=google.com&type=A"),
            ("Quad9 (9.9.9.9)", "https://dns.quad9.net:5053/dns-query?name=google.com&type=A")
        };

        var passedCount = 0;
        var results = new List<string>();

        foreach (var (name, url) in targets)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(1500);

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/dns-json"));
                req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/dns-message"));

                var sw = Stopwatch.StartNew();
                using var resp = await httpClient.SendAsync(req, cts.Token).ConfigureAwait(false);
                sw.Stop();

                if (resp.IsSuccessStatusCode)
                {
                    passedCount++;
                    results.Add($"{name}: OK ({sw.ElapsedMilliseconds} ms)");
                }
                else
                {
                    results.Add($"{name}: HTTP {(int)resp.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                results.Add($"{name}: БЛОКИРОВКА ({ex.Message})");
            }
        }

        var allPassed = passedCount == targets.Length;
        var summary = $"Успешно: {passedCount}/{targets.Length} Anycast узлов. [{string.Join("; ", results)}]";
        return (allPassed, summary);
    }
}
