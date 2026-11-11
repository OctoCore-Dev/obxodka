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

    private static readonly Lazy<string[]> s_internalBrandStems = new(BuildInternalBrandStems);

    private static string[] BuildInternalBrandStems()
    {
        var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void ExtractStem(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain))
            {
                return;
            }

            var parts = domain.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                var sld = parts[^2];
                if (sld.Length >= 4)
                {
                    _ = stems.Add(sld);
                }
            }
        }

        ExtractStem(AppConfig.PrimaryDomain);
        ExtractStem(AppConfig.MirrorDomain);
        foreach (var m in BootstrapConfig.Mirrors)
        {
            ExtractStem(m);
        }

        //резервные xor маскированные токены на случай пустого bootstrap конфига
        Span<byte> b1 = [0x45, 0x48, 0x52, 0x45, 0x4E, 0x41, 0x4B];
        Span<byte> b2 = [0x45, 0x49, 0x5E, 0x45, 0x49, 0x45, 0x58, 0x4F];
        Span<char> c1 = stackalloc char[b1.Length];
        Span<char> c2 = stackalloc char[b2.Length];
        for (var i = 0; i < b1.Length; i++)
        {
            c1[i] = (char)(b1[i] ^ 0x2A);
        }

        for (var i = 0; i < b2.Length; i++)
        {
            c2[i] = (char)(b2[i] ^ 0x2A);
        }
        _ = stems.Add(new string(c1));
        _ = stems.Add(new string(c2));

        return [.. stems];
    }

    //проверка содержит ли хост внутренние адреса проекта которые нельзя светить в открытом sni
    public static bool IsInternalBrandHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var trimmed = host.Trim();
        foreach (var stem in s_internalBrandStems.Value)
        {
            if (trimmed.Contains(stem, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    //проверка является ли эндпоинт облачным щитом cloudflare
    public static bool IsShieldEndpoint(string? serverIp, string? targetSni)
    {
        ReadOnlySpan<string?> candidates = [targetSni, serverIp];
        var primary = AppConfig.PrimaryDomain;
        var mirrorDomain = AppConfig.MirrorDomain;

        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c))
            {
                continue;
            }

            var trimmed = c.Trim();
            if (!string.IsNullOrWhiteSpace(primary) &&
                (trimmed.Equals(primary, StringComparison.OrdinalIgnoreCase) ||
                 trimmed.EndsWith($".{primary}", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(mirrorDomain) &&
                (trimmed.Equals(mirrorDomain, StringComparison.OrdinalIgnoreCase) ||
                 trimmed.EndsWith($".{mirrorDomain}", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            foreach (var mirror in BootstrapConfig.Mirrors)
            {
                if (trimmed.Equals(mirror, StringComparison.OrdinalIgnoreCase) ||
                    trimmed.EndsWith($".{mirror}", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    //определение режима транспорта и целевого sni перед запуском туннеля
    public static SniTransportStrategy ResolveTransportStrategy(string serverIp, string? targetSni, bool forceWebSocket)
    {
        var isShield = IsShieldEndpoint(serverIp, targetSni);
        var isWebSocketMode = isShield || forceWebSocket;

        //для щита cloudflare sni обязателен и совпадает с доменом зоны
        //для прямого подключения к ip сервера отправка sni отключена (zero-sni без ложных sni)
        var effectiveSni = isShield
            ? (!string.IsNullOrWhiteSpace(targetSni) && !IPAddress.TryParse(targetSni, out _) ? targetSni : AppConfig.PrimaryDomain)
            : (isWebSocketMode ? (!string.IsNullOrWhiteSpace(targetSni) ? targetSni : serverIp) : null);

        var clearPinning = isShield;

        return new SniTransportStrategy(isWebSocketMode, effectiveSni, clearPinning);
    }

    //формирование списка кандидатов sni и портов для http2 транспорта
    public static List<SniStreamCandidate> BuildStreamCandidates(string serverIp, int serverPort, string? configuredSni)
    {
        //для прямого подключения zero-sni: целевой хост равен ip сервера (без отправки sni в tls)
        var targetHost = !string.IsNullOrWhiteSpace(configuredSni) && !IsInternalBrandHost(configuredSni)
            ? configuredSni
            : serverIp;

        int[] portCandidates = serverPort == 443
            ? [443, 8443, 2053]
            : [serverPort, 443, 8443, 2053];

        var result = new List<SniStreamCandidate>(portCandidates.Length * 2);
        var attemptIndex = 0;

        foreach (var port in portCandidates)
        {
            //прямой поток без dpi вмешательства
            result.Add(new SniStreamCandidate(targetHost, port, false, RoutingHello.GetProfileForAttempt(attemptIndex++)));
            //поток с l4 сегментацией
            result.Add(new SniStreamCandidate(targetHost, port, true, RoutingHello.GetProfileForAttempt(attemptIndex++)));
        }

        return result;
    }

    //формирование списка кандидатов зеркал и sni для websocket транспорта
    public static List<SniWebSocketCandidate> BuildWebSocketCandidates(string serverIp, int serverPort, string? configuredSni)
    {
        var isShield = IsShieldEndpoint(serverIp, configuredSni);
        var serverIsIp = IPAddress.TryParse(serverIp, out _);
        var sniIsDomain = !string.IsNullOrWhiteSpace(configuredSni) && !IPAddress.TryParse(configuredSni, out _);
        var isLocalLoopback = serverIp.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                              serverIp.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                              serverIp.Equals("::1", StringComparison.OrdinalIgnoreCase);
        var isDomainShieldMode = !isLocalLoopback && (isShield || !serverIsIp || sniIsDomain);

        var activeServerHost = isShield
            ? (sniIsDomain ? configuredSni! : (!serverIsIp ? serverIp : AppConfig.PrimaryDomain))
            : (sniIsDomain ? configuredSni! : (!serverIsIp ? serverIp : AppConfig.PrimaryDomain));

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

            //аварийный прямой выход с zero-sni если заблокирована сама сеть cdn
            AddCandidate(AppConfig.DirectServerIp, AppConfig.DirectServerIp, 443);
            return result;
        }

        //режим прямого ip или локального тестового стенда
        int[] directPorts = serverPort == 443
            ? [443, 8443, 2053]
            : [serverPort, 443, 8443, 2053];

        for (var pIdx = 0; pIdx < directPorts.Length; pIdx++)
        {
            var sni = isLocalLoopback ? activeServerHost : serverIp;
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
