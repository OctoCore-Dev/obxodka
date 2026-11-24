namespace obxodka.Cli;

//снимок активного vpn подключения cli для команд status и disconnect
public sealed record CliRuntimeState(
    int ProcessId,
    string State,
    string? ServerName,
    string? ServerIp,
    int ServerPort,
    string? Protocol,
    DateTimeOffset StartedAtUtc,
    long BytesReceived,
    long BytesSent,
    long DownloadSpeedBps,
    long UploadSpeedBps,
    int Mtu,
    bool KillSwitch,
    bool AdBlock,
    bool BlockIpv6);

//элемент списка серверов для json-вывода
public sealed record CliServerOutput(
    string Id,
    string Name,
    string Location,
    string Ip,
    int Port,
    int? PingMs);

//результат диагностики сети для json-вывода
public sealed record CliDiagOutput(
    string Os,
    bool HasTunnelPrivileges,
    string? TunnelPrivilegeNote,
    bool ApiReachable,
    long ApiLatencyMs,
    string[] ResolvedApiIps,
    int CurrentMtu,
    string? ConflictingVpn);

//информация о профиле для json-вывода
public sealed record CliWhoAmIOutput(
    bool LoggedIn,
    string? Email,
    DateTime? SubscriptionUntilUtc,
    long BalanceSeconds);

//генератор метаданных json для совместимости с trimming и aot
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(CliRuntimeState))]
[JsonSerializable(typeof(CliServerOutput))]
[JsonSerializable(typeof(List<CliServerOutput>))]
[JsonSerializable(typeof(CliDiagOutput))]
[JsonSerializable(typeof(CliWhoAmIOutput))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
