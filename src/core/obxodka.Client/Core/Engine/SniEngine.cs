namespace obxodka.Core;

//стратегия выбора транспорта и sni для подключения
public readonly record struct SniTransportStrategy(
    bool UseWebSocket,
    string? EffectiveSni,
    bool ClearDynamicPinning);

//кандидат подключения для многолучевого http2 транспорта
public readonly record struct SniStreamCandidate(
    string TargetHost,
    int Port,
    bool EnableDpiBypass,
    TlsBrowserProfile BrowserProfile);

//кандидат подключения для websocket транспорта
public readonly record struct SniWebSocketCandidate(
    string ServerHost,
    string TargetSni,
    string HostHeader,
    string ConnectHost,
    int Port,
    string Path,
    TlsBrowserProfile BrowserProfile);

//движок управления sni маскировкой маршрутами и нарезкой сессий
public static class SniEngine
{
    //пути эндпоинтов для распределения http2 потоков под облачную телеметрию и медиа чанки
    private static readonly string[] s_streamPaths =
    [
        "/api/v1/sync",
        "/api/v1/stream",
        "/v1/telemetry",
        "/g/collect",
        "/cdn-cgi/rum",
        "/v2/event"
    ];

    //пути эндпоинтов для websocket подключения
    private static readonly string[] s_wsPaths =
    [
        "/api/v1/stream",
        "/v1/telemetry",
        "/g/collect",
        "/api/v1/sync",
        "/v2/event",
        "/api/v1/ws"
    ];

    //проверка содержит ли хост внутренние маркеры проекта которые нельзя светить в открытом sni
    public static bool IsInternalBrandHost(string? host) =>
        !string.IsNullOrWhiteSpace(host) &&
        (host.Contains("obxodka", StringComparison.OrdinalIgnoreCase) ||
         host.Contains("octocore", StringComparison.OrdinalIgnoreCase));

    //определение режима транспорта и целевого sni перед запуском туннеля
    public static SniTransportStrategy ResolveTransportStrategy(string serverIp, string? targetSni, bool forceWebSocket)
    {
        var isWebSocketMode = serverIp.Equals(AppConfig.PrimaryDomain, StringComparison.OrdinalIgnoreCase) ||
                              serverIp.EndsWith($".{AppConfig.PrimaryDomain}", StringComparison.OrdinalIgnoreCase) ||
                              (targetSni != null && targetSni.Contains(AppConfig.PrimaryDomain, StringComparison.OrdinalIgnoreCase)) ||
                              forceWebSocket;

        var effectiveSni = !string.IsNullOrWhiteSpace(targetSni)
            ? targetSni
            : (isWebSocketMode
                ? AppConfig.PrimaryDomain
                : (IPAddress.TryParse(serverIp, out _) ? AppSecrets.GetRandomSni() : null));

        var clearPinning = isWebSocketMode &&
                           (serverIp.Contains(AppConfig.PrimaryDomain, StringComparison.OrdinalIgnoreCase) ||
                            (targetSni != null && targetSni.Contains(AppConfig.PrimaryDomain, StringComparison.OrdinalIgnoreCase)));

        return new SniTransportStrategy(isWebSocketMode, effectiveSni, clearPinning);
    }

    //формирование списка кандидатов sni и портов для http2 транспорта
    public static List<SniStreamCandidate> BuildStreamCandidates(string serverIp, int serverPort, string? configuredSni)
    {
        var sniCandidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredSni) &&
            !IPAddress.TryParse(configuredSni, out _) &&
            !IsInternalBrandHost(configuredSni))
        {
            sniCandidates.Add(configuredSni);
        }
        else if (!IPAddress.TryParse(serverIp, out _) && !IsInternalBrandHost(serverIp))
        {
            sniCandidates.Add(serverIp);
        }
        else
        {
            sniCandidates.Add(AppSecrets.AllowedSniPool[0]);
        }

        foreach (var candidate in AppSecrets.GetCandidateSniList(8))
        {
            if (!sniCandidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                sniCandidates.Add(candidate);
            }
        }

        int[] portCandidates = serverPort == 443
            ? [443, 8443]
            : [serverPort, 443, 8443];

        var result = new List<SniStreamCandidate>(portCandidates.Length * (sniCandidates.Count + 2));
        var attemptIndex = 0;

        foreach (var port in portCandidates)
        {
            for (var sIdx = 0; sIdx < sniCandidates.Count; sIdx++)
            {
                var sni = sniCandidates[sIdx];
                result.Add(new SniStreamCandidate(sni, port, true, RoutingHello.GetProfileForAttempt(attemptIndex++)));
                if (sIdx < 2 && port == 443)
                {
                    result.Add(new SniStreamCandidate(sni, port, false, RoutingHello.GetProfileForAttempt(attemptIndex++)));
                }
            }
        }

        return result;
    }

    //формирование списка кандидатов зеркал и sni для websocket транспорта
    public static List<SniWebSocketCandidate> BuildWebSocketCandidates(string serverIp, int serverPort, string? configuredSni)
    {
        var serverIsIp = IPAddress.TryParse(serverIp, out _);
        var sniIsDomain = !string.IsNullOrWhiteSpace(configuredSni) && !IPAddress.TryParse(configuredSni, out _);
        var isLocalLoopback = serverIp.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                              serverIp.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                              serverIp.Equals("::1", StringComparison.OrdinalIgnoreCase);
        var isDomainShieldMode = !isLocalLoopback && (!serverIsIp || sniIsDomain);

        var activeServerHost = sniIsDomain
            ? configuredSni!
            : (!serverIsIp ? serverIp : AppConfig.PrimaryDomain);

        var result = new List<SniWebSocketCandidate>(10);
        var attemptIdx = 0;

        void AddCandidate(string serverHost, string targetSni, int port)
        {
            var hostIsIp = IPAddress.TryParse(serverHost, out _);
            var hostHeader = hostIsIp ? targetSni : serverHost;
            var wsPath = s_wsPaths[attemptIdx % s_wsPaths.Length];
            var profile = RoutingHello.GetProfileForAttempt(attemptIdx++);
            result.Add(new SniWebSocketCandidate(
                ServerHost: serverHost,
                TargetSni: targetSni,
                HostHeader: hostHeader,
                ConnectHost: hostHeader,
                Port: port,
                Path: wsPath,
                BrowserProfile: profile));
        }

        if (isDomainShieldMode)
        {
            //сначала основной домен щита затем чистые облачные зеркала на стандартном порту 443
            AddCandidate(activeServerHost, activeServerHost, serverPort);

            var mirrors = AppConfig.MirrorDomains;
            var addedMirrorsOn443 = 0;
            for (var mIdx = 0; mIdx < mirrors.Length && addedMirrorsOn443 < 5; mIdx++)
            {
                var mirror = mirrors[mIdx];
                if (string.IsNullOrWhiteSpace(mirror) ||
                    mirror.Equals(activeServerHost, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AddCandidate(mirror, mirror, 443);
                addedMirrorsOn443++;
            }

            //альтернативные облачные порты cloudflare на случай фильтрации апгрейда на 443
            var altHost8443 = mirrors.Length > 0 ? mirrors[0] : activeServerHost;
            var altHost2053 = mirrors.Length > 1 ? mirrors[1] : activeServerHost;
            AddCandidate(altHost8443, altHost8443, 8443);
            AddCandidate(altHost2053, altHost2053, 2053);

            //аварийный прямой выход с маскировочным sni если заблокирована сама сеть cdn
            var fallbackSni = AppSecrets.GetRandomSni();
            AddCandidate(AppConfig.DirectServerIp, fallbackSni, 443);
            return result;
        }

        //режим прямого ip или локального тестового стенда
        int[] directPorts = serverPort == 443
            ? [443, 8443, 2053]
            : [serverPort, 443, 8443, 2053];

        for (var pIdx = 0; pIdx < directPorts.Length; pIdx++)
        {
            var sni = isLocalLoopback ? activeServerHost : AppSecrets.GetRandomSni();
            AddCandidate(serverIp, sni, directPorts[pIdx]);
        }

        return result;
    }

    //выбор маскировочного пути и заголовка content-type для очередного цикла http2 потока
    public static string GetStreamRoute(int ray, int cycleIndex, out string requestContentType)
    {
        var path = s_streamPaths[Math.Abs(ray + cycleIndex) % s_streamPaths.Length];
        requestContentType = RoutingHello.GetStreamRequestContentType(path);
        return path;
    }

    //расчет параметров нарезки сессии по времени и объему данных для имитации чанков облака и видеопотока
    public static void CalculateStreamSliceWindow(int ray, out long maxDurationTicks, out long maxSliceBytes)
    {
        var durationSeconds = 55 + (ray * 25) + RandomNumberGenerator.GetInt32(10, 36);
        maxDurationTicks = (long)(durationSeconds * (double)Stopwatch.Frequency);

        var baseMegabytes = 24 + (ray * 8) + RandomNumberGenerator.GetInt32(0, 17);
        maxSliceBytes = baseMegabytes * 1024L * 1024L;
    }
}
