namespace obxodka.Benchmarks;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(TelemetryDto))]
[JsonSerializable(typeof(LoginResponse))]
[JsonSerializable(typeof(AuthRequest))]
[JsonSerializable(typeof(DeviceItem))]
internal sealed partial class BenchmarkJsonContext : JsonSerializerContext
{
}

[MemoryDiagnoser]
public class SerializationBenchmarks
{
    private TelemetryDto _telemetry = null!;
    private string _telemetryJson = null!;
    private LoginResponse _loginResponse = null!;
    private string _loginJson = null!;

    [GlobalSetup]
    public void Setup()
    {
        _telemetry = new TelemetryDto("HWID-ABC-12345678", "4.8.0", "Diagnostic Test Trace", "at obxodka.Engine.Start()");
        _telemetryJson = JsonSerializer.Serialize(_telemetry, BenchmarkJsonContext.Default.TelemetryDto);

        _loginResponse = new LoginResponse("JWT.Sample.Token", "VpnConfigData", "0123456789ABCDEF0123456789ABCDEF01234567", DateTime.UtcNow.AddDays(30), 86400, "user@obxodka.one");
        _loginJson = JsonSerializer.Serialize(_loginResponse, BenchmarkJsonContext.Default.LoginResponse);
    }

    [Benchmark(Description = "Serialize TelemetryDto (AOT SourceGen)")]
    public string SerializeTelemetry() => JsonSerializer.Serialize(_telemetry, BenchmarkJsonContext.Default.TelemetryDto);

    [Benchmark(Description = "Deserialize TelemetryDto (AOT SourceGen)")]
    public TelemetryDto? DeserializeTelemetry() => JsonSerializer.Deserialize(_telemetryJson, BenchmarkJsonContext.Default.TelemetryDto);

    [Benchmark(Description = "Serialize LoginResponse (AOT SourceGen)")]
    public string SerializeLogin() => JsonSerializer.Serialize(_loginResponse, BenchmarkJsonContext.Default.LoginResponse);

    [Benchmark(Description = "Deserialize LoginResponse (AOT SourceGen)")]
    public LoginResponse? DeserializeLogin() => JsonSerializer.Deserialize(_loginJson, BenchmarkJsonContext.Default.LoginResponse);
}
