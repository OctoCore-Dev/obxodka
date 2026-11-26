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
    private static readonly Lazy<string[]> s_internalBrandStems = new(BuildInternalBrandStems);

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

    //формирование списка кандидатов зеркал и sni для websocket транспорта
    public static List<SniWebSocketCandidate> BuildWebSocketCandidates(string serverIp, int serverPort, string? configuredSni) =>
        BuildEdgeCandidates(serverIp, serverPort, configuredSni, s_wsPaths);

    //формирование списка кандидатов зеркал и путей для split-http (xhttp) транспорта
    public static List<SniWebSocketCandidate> BuildXhttpCandidates(string serverIp, int serverPort, string? configuredSni) =>
        BuildEdgeCandidates(serverIp, serverPort, configuredSni, s_streamPaths);

    private static string[] BuildInternalBrandStems()
    {
        var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

        void ExtractStem(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain))
            {
                return;
            }

            var parts = domain.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                return;
            }

            var sld = parts[^2];
            if (sld.Length >= 4)
            {
                _ = stems.Add(sld);
            }
        }
    }

    //проверка содержит ли хост внутренние адреса проекта которые нельзя светить в открытом sni
    public static bool IsInternalBrandHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var trimmed = host.Trim();
        return s_internalBrandStems.Value.Any(stem => trimmed.Contains(stem, StringComparison.OrdinalIgnoreCase));
    }

    //проверка является ли эндпоинт облачным щитом cloudflare
    private static bool IsShieldEndpoint(string? serverIp, string? targetSni)
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

            if (BootstrapConfig.Mirrors.Any(mirror => trimmed.Equals(mirror, StringComparison.OrdinalIgnoreCase) ||
                                                      trimmed.EndsWith($".{mirror}", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    //единое определение режима щита, ssl-пиннинга и целевого sni для выбранного сервера
    public static bool ConfigureServerSecurity(VpnServerDto server, out string targetSni)
    {
        ArgumentNullException.ThrowIfNull(server);

        var isShield = string.Equals(server.Role, "shield", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(server.Provider, "Cloudflare", StringComparison.OrdinalIgnoreCase) ||
                       IsShieldEndpoint(server.Ip, server.Ip);

        if (!string.IsNullOrWhiteSpace(server.CertHash))
        {
            OctopusEngine.DirectOriginCertHash = server.CertHash;
        }

        if (isShield)
        {
            OctopusEngine.DynamicSslPublicKeyHash = null;
            targetSni = Uri.CheckHostName(server.Ip) == UriHostNameType.Dns ? server.Ip : AppConfig.PrimaryDomain;
        }
        else
        {
            OctopusEngine.DynamicSslPublicKeyHash =
                !string.IsNullOrWhiteSpace(server.CertHash) ? server.CertHash : OctopusEngine.DirectOriginCertHash;
            targetSni = server.Ip;
        }

        return isShield;
    }

    //определение режима транспорта и целевого sni перед запуском туннеля
    public static SniTransportStrategy ResolveTransportStrategy(string serverIp, string? targetSni, bool forceWebSocket)
    {
        var isShield = IsShieldEndpoint(serverIp, targetSni);
        var isWebSocketMode = isShield || forceWebSocket;

        //для щита cloudflare sni обязателен и совпадает с доменом зоны
        //для прямого подключения к ip сервера отправка sni отключена (zero-sni без ложных sni)
        var effectiveSni = isShield
            ? !string.IsNullOrWhiteSpace(targetSni) && !IPAddress.TryParse(targetSni, out _) ? targetSni : AppConfig.PrimaryDomain
            : isWebSocketMode
                ? !string.IsNullOrWhiteSpace(targetSni) ? targetSni : serverIp
                : null;

        return new SniTransportStrategy(isWebSocketMode, effectiveSni, isShield);
    }

    //формирование списка кандидатов sni и портов для http2 транспорта (dpi bypass в приоритете для быстрого старта в рф)
    public static List<SniStreamCandidate> BuildStreamCandidates(string serverIp, int serverPort, string? configuredSni)
    {
        //для прямого подключения zero-sni: целевой хост равен ip сервера (без отправки sni в tls)
        var targetHost = !string.IsNullOrWhiteSpace(configuredSni) && !IsInternalBrandHost(configuredSni)
            ? configuredSni
            : serverIp;

        int[] portCandidates = serverPort == 443
            ? [443, 8443, 2053]
            : [serverPort, 443, 8443];

        var result = new List<SniStreamCandidate>(portCandidates.Length + 1);
        var attemptIndex = 0;

        result.AddRange(portCandidates.Select(port =>
            new SniStreamCandidate(targetHost, port, true, RoutingHello.GetProfileForAttempt(attemptIndex++))));

        //резервный прямой поток на случай если сегментация clienthello не требуется сетью
        result.Add(new SniStreamCandidate(targetHost, portCandidates[0], false, RoutingHello.GetProfileForAttempt(attemptIndex)));
        return result;
    }

    //формирование кругового маршрута протоколов: obxodka stream -> websocket -> xhttp -> резервные узлы -> по кругу
    public static List<ProtocolRouteStep> BuildProtocolRotationRing(
        IReadOnlyList<VpnServerDto>? servers,
        string? primaryHost,
        int primaryPort,
        string? configuredSni,
        bool forceWebSocket)
    {
        List<VpnServerDto> normalizedServers = servers is { Count: > 0 }
            ? [.. servers]
            : string.IsNullOrWhiteSpace(primaryHost)
                ? []
                : [new VpnServerDto(primaryHost, primaryPort > 0 ? primaryPort : 443, "", true, 0)];

        foreach (var srv in normalizedServers.Where(srv => !string.IsNullOrWhiteSpace(srv.CertHash)))
        {
            OctopusEngine.DirectOriginCertHash = srv.CertHash;
            break;
        }

        List<ProtocolRouteStep> directStreamSteps = [];
        List<ProtocolRouteStep> shieldWsSteps = [];
        List<ProtocolRouteStep> shieldXhttpSteps = [];
        var seenDirectHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var srv in normalizedServers.Where(srv => !IsShieldEndpoint(srv.Ip, srv.Ip) &&
                                                           !string.Equals(srv.Role, "shield", StringComparison.OrdinalIgnoreCase) &&
                                                           !string.Equals(srv.Provider, "Cloudflare", StringComparison.OrdinalIgnoreCase)))
        {
            TryAddDirectServer(srv.Ip, srv.Port, srv.Location, srv.CertHash);
        }

        if (!string.IsNullOrWhiteSpace(AppConfig.DirectServerIp) &&
            !AppConfig.DirectServerIp.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            TryAddDirectServer(AppConfig.DirectServerIp, 443, "Direct Origin", OctopusEngine.DirectOriginCertHash);
        }

        var preferredShieldHost = PickPreferredShieldHost(normalizedServers, configuredSni);
        shieldWsSteps.Add(new ProtocolRouteStep(
            TransportProtocolKind.ObxodkaWebSocket,
            preferredShieldHost,
            443,
            preferredShieldHost,
            true,
            true,
            "Cloudflare WS"));

        shieldXhttpSteps.Add(new ProtocolRouteStep(
            TransportProtocolKind.ObxodkaXhttp,
            preferredShieldHost,
            443,
            preferredShieldHost,
            true,
            true,
            "Cloudflare XHTTP"));

        //резервный xhttp и ws напрямую к origin ip на случай полной блокировки всех cdn в регионе
        shieldXhttpSteps.AddRange(directStreamSteps.Select(direct => new ProtocolRouteStep(TransportProtocolKind.ObxodkaXhttp, direct.Host,
            direct.Port, direct.Host, false, false, $"{direct.ServerLabel ?? direct.Host} (XHTTP)", direct.ExpectedCertHash)));

        List<ProtocolRouteStep> penalizedDirect = [];
        List<ProtocolRouteStep> penalizedDirectReality = [];
        List<ProtocolRouteStep> penalizedDirectXhttp = [];

        //стелс-шаги reality на стандартном порту 443 для каждого прямого узла
        List<ProtocolRouteStep> directRealitySteps = [];
        foreach (var direct in directStreamSteps)
        {
            var realityStep = new ProtocolRouteStep(
                TransportProtocolKind.ObxodkaReality,
                direct.Host,
                direct.Port,
                null,
                false,
                false,
                $"{direct.ServerLabel ?? direct.Host} (Reality)",
                direct.ExpectedCertHash);

            if (OctopusEngine.IsNodeByteCapped(direct.Host))
            {
                penalizedDirectReality.Add(realityStep);
            }
            else
            {
                directRealitySteps.Add(realityStep);
            }
        }

        //резервный xhttp напрямую к origin ip, если прямой узел еще не заблокирован, выносим в конец кольца после щитов
        List<ProtocolRouteStep> directXhttpSteps = [];
        foreach (var step in shieldXhttpSteps)
        {
            if (!step.IsShield && OctopusEngine.IsNodeByteCapped(step.Host))
            {
                penalizedDirectXhttp.Add(step);
            }
            else
            {
                directXhttpSteps.Add(step);
            }
        }

        List<ProtocolRouteStep> ring =
            [with(directStreamSteps.Count + directRealitySteps.Count + shieldWsSteps.Count + shieldXhttpSteps.Count)];

        if (!forceWebSocket)
        {
            foreach (var step in directStreamSteps)
            {
                if (OctopusEngine.IsNodeByteCapped(step.Host))
                {
                    penalizedDirect.Add(step);
                }
                else
                {
                    ring.Add(step);
                }
            }

            ring.AddRange(directRealitySteps);
        }

        ring.AddRange(shieldWsSteps);
        ring.AddRange(directXhttpSteps);

        if (forceWebSocket)
        {
            return ring;
        }

        ring.AddRange(penalizedDirectXhttp);
        ring.AddRange(penalizedDirectReality);
        ring.AddRange(penalizedDirect);

        return ring;

        void TryAddDirectServer(string host, int port, string? label, string? certHash)
        {
            if (string.IsNullOrWhiteSpace(host) || IsShieldEndpoint(host, host) || !seenDirectHosts.Add(host))
            {
                return;
            }

            directStreamSteps.Add(new ProtocolRouteStep(
                TransportProtocolKind.ObxodkaStream,
                host,
                port > 0 ? port : 443,
                null,
                false,
                false,
                label,
                !string.IsNullOrWhiteSpace(certHash) ? certHash : OctopusEngine.DirectOriginCertHash));
        }
    }

    //выбор оптимального домена щита с приоритетом не-188.114 пула octocore.dev
    private static string PickPreferredShieldHost(IReadOnlyList<VpnServerDto> servers, string? configuredSni)
    {
        var mirrors = GetPrioritizedMirrorHosts();
        if (mirrors.Count > 0)
        {
            return mirrors[0];
        }

        if (!string.IsNullOrWhiteSpace(configuredSni) && IsShieldEndpoint(configuredSni, configuredSni))
        {
            return configuredSni;
        }

        foreach (var s in servers)
        {
            if (IsShieldEndpoint(s.Ip, s.Ip))
            {
                return s.Ip;
            }
        }

        return AppConfig.PrimaryDomain;
    }

    //сортировка зеркал: чередование пулов с приоритетом альтернативных корневых доменов перед primary (188.114.96.x)
    private static List<string> GetPrioritizedMirrorHosts()
    {
        var primary = AppConfig.PrimaryDomain;
        List<string> altPools = [];
        List<string> primaryPool = [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in AppConfig.MirrorDomains)
        {
            Classify(m);
        }

        Classify(primary);

        List<string> merged = [with(altPools.Count + primaryPool.Count)];
        var maxCount = Math.Max(altPools.Count, primaryPool.Count);
        for (var i = 0; i < maxCount; i++)
        {
            if (i < altPools.Count)
            {
                merged.Add(altPools[i]);
            }

            if (i < primaryPool.Count)
            {
                merged.Add(primaryPool[i]);
            }
        }

        return merged;

        void Classify(string? host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return;
            }

            var trimmed = host.Trim();
            if (!seen.Add(trimmed))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(primary) &&
                (trimmed.Equals(primary, StringComparison.OrdinalIgnoreCase) ||
                 trimmed.EndsWith($".{primary}", StringComparison.OrdinalIgnoreCase)))
            {
                primaryPool.Add(trimmed);
            }
            else
            {
                altPools.Add(trimmed);
            }
        }
    }

    //единый генератор кандидатов для облачных транспортов (ws и xhttp) с ротацией пулов и резервным выходом
    private static List<SniWebSocketCandidate> BuildEdgeCandidates(string serverIp, int serverPort, string? configuredSni, string[] paths)
    {
        var isShield = IsShieldEndpoint(serverIp, configuredSni);
        var serverIsIp = IPAddress.TryParse(serverIp, out _);
        var sniIsDomain = !string.IsNullOrWhiteSpace(configuredSni) && !IPAddress.TryParse(configuredSni, out _);
        var isLocalLoopback = serverIp.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                              serverIp.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                              serverIp.Equals("::1", StringComparison.OrdinalIgnoreCase);
        var isDomainShieldMode = !isLocalLoopback && (isShield || !serverIsIp || sniIsDomain);

        var activeServerHost = sniIsDomain ? configuredSni! : !serverIsIp ? serverIp : AppConfig.PrimaryDomain;

        var result = new List<SniWebSocketCandidate>(12);
        var attemptIdx = 0;

        if (isDomainShieldMode)
        {
            var prioritizedMirrors = GetPrioritizedMirrorHosts();
            if (sniIsDomain)
            {
                AddCandidate(activeServerHost, activeServerHost, serverPort);
            }

            var addedMirrorsOn443 = 0;
            for (var mIdx = 0; mIdx < prioritizedMirrors.Count && addedMirrorsOn443 < 7; mIdx++)
            {
                var mirror = prioritizedMirrors[mIdx];
                if (sniIsDomain && serverPort == 443 && mirror.Equals(activeServerHost, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AddCandidate(mirror, mirror, 443);
                addedMirrorsOn443++;
            }

            var altHost8443 = prioritizedMirrors.Count > 0 ? prioritizedMirrors[0] : activeServerHost;
            var altHost2053 = prioritizedMirrors.Count > 1 ? prioritizedMirrors[1] : activeServerHost;
            AddCandidate(altHost8443, altHost8443, 8443);
            AddCandidate(altHost2053, altHost2053, 2053);

            //аварийный прямой выход с zero-sni если заблокирована сама сеть cdn
            if (!string.IsNullOrWhiteSpace(AppConfig.DirectServerIp))
            {
                AddCandidate(AppConfig.DirectServerIp, AppConfig.DirectServerIp, 443);
            }

            return result;
        }

        //режим прямого ip или локального тестового стенда
        int[] directPorts = serverPort == 443
            ? [443, 8443, 2053]
            : [serverPort, 443, 8443, 2053];

        var directSni = isLocalLoopback ? activeServerHost : serverIp;
        foreach (var port in directPorts)
        {
            AddCandidate(serverIp, directSni, port);
        }

        return result;

        void AddCandidate(string serverHost, string targetSni, int port)
        {
            var hostIsIp = IPAddress.TryParse(serverHost, out _);
            var hostHeader = hostIsIp ? targetSni : serverHost;
            var routePath = paths[attemptIdx % paths.Length];
            var profile = RoutingHello.GetProfileForAttempt(attemptIdx++);
            result.Add(new SniWebSocketCandidate(
                serverHost,
                targetSni,
                hostHeader,
                hostHeader,
                port,
                routePath,
                profile));
        }
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
        var durationSeconds = 55 + ray * 25 + RandomNumberGenerator.GetInt32(10, 36);
        maxDurationTicks = (long)(durationSeconds * (double)Stopwatch.Frequency);

        var baseMegabytes = 24 + ray * 8 + RandomNumberGenerator.GetInt32(0, 17);
        maxSliceBytes = baseMegabytes * 1024L * 1024L;
    }
}
