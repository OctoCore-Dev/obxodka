namespace obxodka.Core;

//поддерживаемые типы транспортных протоколов туннеля
public enum TransportProtocolKind
{
    ObxodkaStream = 0,
    ObxodkaWebSocket = 1,
    ObxodkaXhttp = 2
}

//описание одного шага в круговом маршруте протоколов и серверов
public sealed record ProtocolRouteStep(
    TransportProtocolKind Kind,
    string Host,
    int Port,
    string? TargetSni,
    bool ClearDynamicPinning,
    bool IsShield,
    string? ServerLabel = null);

//контракт расширяемой фабрики транспортных протоколов
public interface ITransportProtocolFactory
{
    public TransportProtocolKind Kind { get; }

    public string DisplayName { get; }

    public IVpnTransport CreateTransport(ProtocolRouteStep step, string? preferredAssignedIp);
}

//глобальный реестр фабрик протоколов для простого подключения новых транспортов в будущем
public static class ProtocolPipelineRegistry
{
    private static readonly ConcurrentDictionary<TransportProtocolKind, ITransportProtocolFactory> s_factories = new();

    static ProtocolPipelineRegistry()
    {
        RegisterFactory(new ObxodkaStreamProtocolFactory());
        RegisterFactory(new ObxodkaWebSocketProtocolFactory());
        RegisterFactory(new ObxodkaXhttpProtocolFactory());
    }

    //регистрация или замена фабрики конкретного протокола
    public static void RegisterFactory(ITransportProtocolFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        s_factories[factory.Kind] = factory;
    }

    //получение зарегистрированной фабрики
    public static bool TryGetFactory(TransportProtocolKind kind, [NotNullWhen(true)] out ITransportProtocolFactory? factory) =>
        s_factories.TryGetValue(kind, out factory);

    //создание экземпляра транспорта для указанного шага кругового маршрута
    public static IVpnTransport CreateTransport(ProtocolRouteStep step, string? preferredAssignedIp = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        return s_factories.TryGetValue(step.Kind, out var factory)
            ? factory.CreateTransport(step, preferredAssignedIp)
            : throw new NotSupportedException($"Транспортный протокол '{step.Kind}' не зарегистрирован в конвейере.");
    }
}

internal sealed class ObxodkaStreamProtocolFactory : ITransportProtocolFactory
{
    public TransportProtocolKind Kind => TransportProtocolKind.ObxodkaStream;
    public string DisplayName => "OBXODKA-STREAM";

    public IVpnTransport CreateTransport(ProtocolRouteStep step, string? preferredAssignedIp) =>
        new ObxodkaStreamTransport(step.Port, step.TargetSni, preferredAssignedIp);
}

internal sealed class ObxodkaWebSocketProtocolFactory : ITransportProtocolFactory
{
    public TransportProtocolKind Kind => TransportProtocolKind.ObxodkaWebSocket;
    public string DisplayName => "OBXODKA-WS";

    public IVpnTransport CreateTransport(ProtocolRouteStep step, string? preferredAssignedIp) =>
        new ObxodkaWebSocketTransport(step.Port, step.TargetSni, preferredAssignedIp);
}

internal sealed class ObxodkaXhttpProtocolFactory : ITransportProtocolFactory
{
    public TransportProtocolKind Kind => TransportProtocolKind.ObxodkaXhttp;
    public string DisplayName => "OBXODKA-XHTTP";

    public IVpnTransport CreateTransport(ProtocolRouteStep step, string? preferredAssignedIp) =>
        new ObxodkaXhttpTransport(step.Port, step.TargetSni, preferredAssignedIp);
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

        var until = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * cooldownSeconds);
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

        List<IPAddress> healthy = new(addresses.Length);
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

        List<IPAddress> ordered = new(addresses.Length);
        for (var i = 0; i < healthy.Count; i++)
        {
            ordered.Add(healthy[(i + shift) % healthy.Count]);
        }
        ordered.AddRange(penalized);
        return [.. ordered];
    }
}
