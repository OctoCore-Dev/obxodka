namespace obxodka.Core;

//закрытая иерархия активного транспорта для типобезопасного отображения лучей в c# 15
public closed record ActiveTransportTag;

public sealed record StreamTransportTag(int ActiveRays) : ActiveTransportTag;

public sealed record WebSocketTransportTag : ActiveTransportTag
{
    public static WebSocketTransportTag Instance { get; } = new();
}

public sealed record XhttpTransportTag : ActiveTransportTag
{
    public static XhttpTransportTag Instance { get; } = new();
}

public sealed record RealityTransportTag : ActiveTransportTag
{
    public static RealityTransportTag Instance { get; } = new();
}

//поддерживаемые типы транспортных протоколов туннеля
public enum TransportProtocolKind
{
    ObxodkaStream = 0,
    ObxodkaWebSocket = 1,
    ObxodkaXhttp = 2,
    ObxodkaReality = 3
}

//описание одного шага в круговом маршруте протоколов и серверов
public sealed record ProtocolRouteStep(
    TransportProtocolKind Kind,
    string Host,
    int Port,
    string? TargetSni,
    bool ClearDynamicPinning,
    bool IsShield,
    string? ServerLabel = null,
    string? ExpectedCertHash = null);

//контракт расширяемой фабрики транспортных протоколов
public interface ITransportProtocolFactory
{
    public TransportProtocolKind Kind { get; }

    public string DisplayName { get; }

    public string FriendlyName => DisplayName;

    public string ShortTag => FriendlyName;

    public string TransportSummary => DisplayName;

    public string Description => string.Empty;

    public int Order => (int)Kind;

    public IVpnTransport CreateTransport(ProtocolRouteStep step, string? preferredAssignedIp);
}

//глобальный реестр фабрик протоколов для простого подключения новых транспортов в будущем
public static class ProtocolPipelineRegistry
{
    public const string AutoProtocolId = "AUTO";

    private static readonly ConcurrentDictionary<TransportProtocolKind, ITransportProtocolFactory> s_factories = new();

    static ProtocolPipelineRegistry()
    {
        RegisterFactory(new ObxodkaStreamProtocolFactory());
        RegisterFactory(new ObxodkaWebSocketProtocolFactory());
        RegisterFactory(new ObxodkaXhttpProtocolFactory());
        RegisterFactory(new ObxodkaRealityProtocolFactory());
    }

    //разбор пользовательского имени или алиаса протокола в канонический идентификатор ssot
    public static bool TryParsePreferredProtocol(string? rawProtocol, out string normalizedProtocol)
    {
        if (string.IsNullOrWhiteSpace(rawProtocol))
        {
            normalizedProtocol = AutoProtocolId;
            return true;
        }

        var trimmed = rawProtocol.Trim();
        switch (trimmed.ToLowerInvariant())
        {
            case "auto" or "авто" or "default":
                normalizedProtocol = AutoProtocolId;
                return true;
            case "stream" or "h2" or "http2" or "obxodka-stream":
                normalizedProtocol = "OBXODKA-STREAM";
                return true;
            case "ws" or "wss" or "websocket" or "cloudflare" or "obxodka-ws":
                normalizedProtocol = "OBXODKA-WS";
                return true;
            case "xhttp" or "split-http" or "splithttp" or "obxodka-xhttp":
                normalizedProtocol = "OBXODKA-XHTTP";
                return true;
            case "reality" or "v8" or "obx-v8" or "obxodka-reality":
                normalizedProtocol = "OBXODKA-REALITY";
                return true;
        }

        if (TryFindFactoryByProtocolName(trimmed, out var factory))
        {
            normalizedProtocol = factory.DisplayName;
            return true;
        }

        normalizedProtocol = AutoProtocolId;
        return false;
    }

    //нормализация предпочтительного протокола с безопасным откатом в auto
    public static string NormalizePreferredProtocol(string? rawProtocol) =>
        TryParsePreferredProtocol(rawProtocol, out var normalized) ? normalized : AutoProtocolId;

    //чтение выбранного протокола из единого хранилища настроек с обратной совместимостью по forcewebsocket
    public static string GetPreferredProtocol(IPreferencesService? preferences = null)
    {
        var prefs = preferences ?? PlatformServices.Preferences;
        var stored = prefs.Get(AppPreferenceKeys.PreferredProtocol, string.Empty);
        return !string.IsNullOrWhiteSpace(stored)
            ? NormalizePreferredProtocol(stored)
            : prefs.Get(AppPreferenceKeys.ForceWebSocket, false)
                ? "OBXODKA-WS"
                : AutoProtocolId;
    }

    //сохранение выбранного протокола и синхронизация старого флага forcewebsocket
    public static string SetPreferredProtocol(string? protocolId, IPreferencesService? preferences = null)
    {
        var prefs = preferences ?? PlatformServices.Preferences;
        var normalized = NormalizePreferredProtocol(protocolId);
        prefs.Set(AppPreferenceKeys.PreferredProtocol, normalized);
        prefs.Set(AppPreferenceKeys.ForceWebSocket, string.Equals(normalized, "OBXODKA-WS", StringComparison.OrdinalIgnoreCase));
        return normalized;
    }

    //преобразование выбранного протокола в конкретный тип транспорта или false для автоматической ротации
    public static bool TryResolvePreferredKind(string? preferredProtocol, out TransportProtocolKind kind)
    {
        if (string.IsNullOrWhiteSpace(preferredProtocol))
        {
            kind = default;
            return false;
        }

        var normalized = NormalizePreferredProtocol(preferredProtocol);
        if (string.Equals(normalized, AutoProtocolId, StringComparison.OrdinalIgnoreCase))
        {
            kind = default;
            return false;
        }

        if (TryFindFactoryByProtocolName(normalized, out var factory))
        {
            kind = factory.Kind;
            return true;
        }

        kind = default;
        return false;
    }

    //регистрация или замена фабрики конкретного протокола
    public static void RegisterFactory(ITransportProtocolFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        s_factories[factory.Kind] = factory;
    }

    //удаление временной фабрики протокола из реестра (для изоляции тестов)
    internal static bool UnregisterFactory(TransportProtocolKind kind) => s_factories.TryRemove(kind, out _);

    //получение всех зарегистрированных фабрик протоколов в стабильном порядке приоритета
    public static IReadOnlyList<ITransportProtocolFactory> GetRegisteredFactories() =>
        [.. s_factories.Values.OrderBy(f => f.Order).ThenBy(f => f.Kind)];

    //получение зарегистрированной фабрики
    public static bool TryGetFactory(TransportProtocolKind kind, [NotNullWhen(true)] out ITransportProtocolFactory? factory) =>
        s_factories.TryGetValue(kind, out factory);

    //поиск фабрики протокола по внутреннему имени транспорта (например OBXODKA-STREAM / OBXODKA-WS / OBXODKA-XHTTP)
    public static bool TryFindFactoryByProtocolName(string? protocolName, [NotNullWhen(true)] out ITransportProtocolFactory? factory)
    {
        if (!string.IsNullOrWhiteSpace(protocolName))
        {
            foreach (var item in s_factories.Values)
            {
                if (string.Equals(item.DisplayName, protocolName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.Kind.ToString(), protocolName, StringComparison.OrdinalIgnoreCase))
                {
                    factory = item;
                    return true;
                }
            }
        }

        factory = null;
        return false;
    }

    //разрешение активного тега транспорта для ui без строковых костылей
    public static ActiveTransportTag ResolveActiveTag(string? protocolName, int activeRays) =>
        protocolName switch
        {
            "OBXODKA-WS" => WebSocketTransportTag.Instance,
            "OBXODKA-XHTTP" => XhttpTransportTag.Instance,
            "OBXODKA-REALITY" => RealityTransportTag.Instance,
            _ => new StreamTransportTag(activeRays)
        };

    //форматирование краткого бейджа активного протокола для верхней плашки ui
    public static string FormatLiveStatusBadge(string? protocolName, int activeRays)
    {
        if (string.IsNullOrWhiteSpace(protocolName) ||
            string.Equals(protocolName, "AUTO", StringComparison.OrdinalIgnoreCase))
        {
            return "Подбор...";
        }

        if (TryFindFactoryByProtocolName(protocolName, out var customFactory) &&
            customFactory.Kind is not (TransportProtocolKind.ObxodkaStream or TransportProtocolKind.ObxodkaWebSocket
                or TransportProtocolKind.ObxodkaXhttp or TransportProtocolKind.ObxodkaReality))
        {
            return customFactory.ShortTag;
        }

#pragma warning disable IDE0072 // C# 15 closed hierarchy is compiler-exhaustive (CS8509)
        return ResolveActiveTag(protocolName, activeRays) switch
        {
            StreamTransportTag stream => $"Stream • {Math.Max(1, stream.ActiveRays)}L",
            WebSocketTransportTag => "WebSocket",
            XhttpTransportTag => "XHTTP",
            RealityTransportTag => "Reality"
        };
#pragma warning restore IDE0072
    }

    //создание экземпляра транспорта для указанного шага кругового маршрута с автозащитой сокетов
    public static IVpnTransport CreateTransport(ProtocolRouteStep step, string? preferredAssignedIp = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        var transport = s_factories.TryGetValue(step.Kind, out var factory)
            ? factory.CreateTransport(step, preferredAssignedIp)
            : throw new NotSupportedException($"Транспортный протокол '{step.Kind}' не зарегистрирован в конвейере.");
        transport.ProtectSockets(OctopusEngine.Current.ProtectSocket);
        return transport;
    }
}

internal sealed class ObxodkaStreamProtocolFactory : ITransportProtocolFactory
{
    public TransportProtocolKind Kind => TransportProtocolKind.ObxodkaStream;
    public string DisplayName => "OBXODKA-STREAM";
    public string FriendlyName => "Obxodka Stream";
    public string ShortTag => "Stream";
    public string TransportSummary => "HTTP/2 TLS 443";
    public string Description => "Многолучевой защищённый дуплексный поток с минимальной задержкой и обходом DPI.";
    public int Order => 0;

    public IVpnTransport CreateTransport(ProtocolRouteStep step, string? preferredAssignedIp) =>
        new ObxodkaStreamTransport(step.Port, step.TargetSni, preferredAssignedIp);
}

internal sealed class ObxodkaWebSocketProtocolFactory : ITransportProtocolFactory
{
    public TransportProtocolKind Kind => TransportProtocolKind.ObxodkaWebSocket;
    public string DisplayName => "OBXODKA-WS";
    public string FriendlyName => "WebSocket (Cloudflare)";
    public string ShortTag => "WebSocket";
    public string TransportSummary => "WSS CDN 443";
    public string Description => "Защищённый облачный щит через WebSocket поверх CDN для обхода жёстких блокировок.";
    public int Order => 1;

    public IVpnTransport CreateTransport(ProtocolRouteStep step, string? preferredAssignedIp) =>
        new ObxodkaWebSocketTransport(step.Port, step.TargetSni, preferredAssignedIp);
}

internal sealed class ObxodkaXhttpProtocolFactory : ITransportProtocolFactory
{
    public TransportProtocolKind Kind => TransportProtocolKind.ObxodkaXhttp;
    public string DisplayName => "OBXODKA-XHTTP";
    public string FriendlyName => "XHTTP (Split-HTTP)";
    public string ShortTag => "XHTTP";
    public string TransportSummary => "Split-HTTP 443";
    public string Description => "Разделённый HTTP-транспорт (GET/POST) для устойчивой работы при агрессивной фильтрации.";
    public int Order => 2;

    public IVpnTransport CreateTransport(ProtocolRouteStep step, string? preferredAssignedIp) =>
        new ObxodkaXhttpTransport(step.Port, step.TargetSni, preferredAssignedIp);
}

internal sealed class ObxodkaRealityProtocolFactory : ITransportProtocolFactory
{
    public TransportProtocolKind Kind => TransportProtocolKind.ObxodkaReality;
    public string DisplayName => "OBXODKA-REALITY";
    public string FriendlyName => "Obxodka Reality";
    public string ShortTag => "Reality";
    public string TransportSummary => "TLS 1.3 Reality 443";
    public string Description => "Прямой стелс-протокол с проверкой SessionID до TLS и маскировкой под легальный сайт.";
    public int Order => 3;

    public IVpnTransport CreateTransport(ProtocolRouteStep step, string? preferredAssignedIp) =>
        new ObxodkaRealityTransport(step.Port, step.TargetSni, preferredAssignedIp);
}

//учет заблокированных edge ip cdn (например 188.114.96.1) для мгновенной ротации к другим адресам
internal static class EdgeEndpointHealth
{
    private static readonly ConcurrentDictionary<string, long> s_unhealthyUntilTicks = new(StringComparer.OrdinalIgnoreCase);
    private static int s_rotationCounter;

    public static void MarkUnhealthy(string? ipAddress, int cooldownSeconds = 120)
    {
        if (string.IsNullOrWhiteSpace(ipAddress) || !IPAddress.TryParse(ipAddress, out var ip) || IPAddress.IsLoopback(ip))
        {
            return;
        }

        var until = Stopwatch.GetTimestamp() + Stopwatch.Frequency * cooldownSeconds;
        s_unhealthyUntilTicks[ip.ToString()] = until;
    }

    public static void ClearAll() => s_unhealthyUntilTicks.Clear();

    public static bool IsUnhealthy(string ipAddress)
    {
        if (!s_unhealthyUntilTicks.TryGetValue(ipAddress, out var until))
        {
            return false;
        }

        if (Stopwatch.GetTimestamp() < until)
        {
            return true;
        }

        _ = s_unhealthyUntilTicks.TryRemove(ipAddress, out _);
        return false;
    }

    public static IPAddress[] FilterAndRotate(IPAddress[] addresses)
    {
        if (addresses.Length <= 1)
        {
            return addresses;
        }

        List<IPAddress> healthy = [with(addresses.Length)];
        List<IPAddress> penalized = [];

        foreach (var addr in addresses)
        {
            if (IsUnhealthy(addr.ToString()))
            {
                penalized.Add(addr);
            }
            else
            {
                healthy.Add(addr);
            }
        }

        var shift = healthy.Count > 1
            ? Math.Abs(Interlocked.Increment(ref s_rotationCounter)) % healthy.Count
            : 0;

        List<IPAddress> ordered =
        [
            with(addresses.Length),
            .. healthy.Select((_, i) => healthy[(i + shift) % healthy.Count]),

            .. penalized
        ];

        return [.. ordered];
    }
}
