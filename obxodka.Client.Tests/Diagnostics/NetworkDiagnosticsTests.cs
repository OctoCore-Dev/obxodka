namespace obxodka.Client.Tests.Diagnostics;

[Trait("Category", "Unit")]
public class NetworkDiagnosticsTests
{
    [Fact]
    public void DiagnosticReportToFormattedTextGeneratesReadableReport()
    {
        var report = new DiagnosticReport
        {
            PrimaryInterfaceName = "Ethernet 1",
            PrimaryInterfaceMtu = 1492,
            DefaultGateway = "192.168.1.1",
            DetectedManagementSoftware = "AnyDesk (PID: 1234)",
            RecommendedProtocol = "HTTP2 (DpiBypass)",
            SummaryVerdict = "ОБНАРУЖЕН МОСКОВСКИЙ ТСПУ: Прямой TLS сбрасывается по признаку несоответствия SNI/IP."
        };

        report.Steps.Add(new DiagnosticStepResult("1. Локальная сеть", DiagnosticStatus.Passed, "OK", TimeSpan.FromMilliseconds(5)));
        report.Steps.Add(new DiagnosticStepResult("2. DNS", DiagnosticStatus.Passed, "OK", TimeSpan.FromMilliseconds(15)));
        report.Steps.Add(new DiagnosticStepResult("3. TCP 443", DiagnosticStatus.Passed, "RTT: 20ms", TimeSpan.FromMilliseconds(20)));
        report.Steps.Add(new DiagnosticStepResult("4A. TLS Прямой", DiagnosticStatus.Failed, "ТСПУ сбросил соединение (TCP RST 10054)", TimeSpan.FromMilliseconds(45)));
        report.Steps.Add(new DiagnosticStepResult("4B. TLS с DpiBypass", DiagnosticStatus.Passed, "Рукопожатие успешно", TimeSpan.FromMilliseconds(75)));
        report.Steps.Add(new DiagnosticStepResult("5. UDP 6767", DiagnosticStatus.Failed, "Таймаут", TimeSpan.FromMilliseconds(1500)));

        var text = report.ToFormattedText();

        Assert.Contains("ОБХОДКА: ПОЛНЫЙ ОТЧЁТ ДИАГНОСТИКИ СЕТИ ТЕСТЕРА", text);
        Assert.Contains("Ethernet 1", text);
        Assert.Contains("1492", text);
        Assert.Contains("[PASS]", text);
        Assert.Contains("1. Локальная сеть", text);
        Assert.Contains("[BLOCKED]", text);
        Assert.Contains("4A. TLS Прямой", text);
        Assert.Contains("4B. TLS с DpiBypass", text);
        Assert.Contains("5. UDP 6767", text);
        Assert.Contains("ОБНАРУЖЕН МОСКОВСКИЙ ТСПУ", text);
        Assert.Contains("HTTP2 (DpiBypass)", text);
    }

    [Fact]
    public void DpiBypassStreamSplit2PreservesPayloadIntegrity()
    {
        using var mem = new MemoryStream();
        using var stream = new DpiBypassStream(mem, splitPosition: 2, delayMs: 1);

        var payload = new byte[] { 0x16, 0x03, 0x01, 0x00, 0x40, 0x01, 0x00 };
        stream.Write(payload);
        stream.Flush();

        Assert.Equal(payload, mem.ToArray());
    }

    [Fact]
    public async Task DpiBypassStreamSplit2AsyncPreservesPayloadIntegrityAsync()
    {
        using var mem = new MemoryStream();
        using var stream = new DpiBypassStream(mem, splitPosition: 2, delayMs: 1);

        var payload = new byte[] { 0x16, 0x03, 0x01, 0x00, 0x40, 0x01, 0x00, 0x99, 0x88 };
        await stream.WriteAsync(payload.AsMemory());
        await stream.FlushAsync();

        Assert.Equal(payload, mem.ToArray());
        Assert.Equal(2, stream.SplitPosition);
        Assert.Equal(1, stream.DelayMs);
    }

    [Fact]
    public async Task NetworkDiagnosticsServiceRunFullDiagnosticsExecutesStepsAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var diagService = new NetworkDiagnosticsService();

        var completedSteps = new List<DiagnosticStepResult>();
        var report = await diagService.RunFullDiagnosticsAsync(
            serverHost: "127.0.0.1",
            serverPort: 443,
            udpPort: 6767,
            onStepCompleted: s => completedSteps.Add(s),
            ct: cts.Token);

        Assert.NotNull(report);
        Assert.NotEmpty(report.Steps);
        Assert.True(completedSteps.Count >= 3);
        Assert.Contains(report.Steps, s => s.StepName.Contains("Локальная сеть"));
        Assert.Contains(report.Steps, s => s.StepName.Contains("Системный DNS"));
        Assert.Contains(report.Steps, s => s.StepName.Contains("безопасного MTU"));
        Assert.False(string.IsNullOrWhiteSpace(report.SummaryVerdict));
        Assert.False(string.IsNullOrWhiteSpace(report.RecommendedProtocol));
    }

    [Fact]
    public async Task LiveNetworkDiagnosticsAgainstProductionVpsAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var diagService = new NetworkDiagnosticsService();

        var report = await diagService.RunFullDiagnosticsAsync(
            serverHost: "45.63.117.29",
            serverPort: 443,
            udpPort: 6767,
            ct: cts.Token);

        Assert.NotNull(report);
        Assert.NotEmpty(report.Steps);
        var formatted = report.ToFormattedText();
        Assert.Contains("ОБХОДКА: ПОЛНЫЙ ОТЧЁТ ДИАГНОСТИКИ СЕТИ ТЕСТЕРА", formatted);
    }
}
