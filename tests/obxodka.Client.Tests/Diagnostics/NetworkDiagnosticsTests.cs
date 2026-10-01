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
            RecommendedProtocol = "HTTP2",
            SummaryVerdict = "СЕТЬ ЧИСТАЯ: TLS-рукопожатие проходит без помех со стороны ТСПУ."
        };

        report.Steps.Add(new DiagnosticStepResult("1. Локальная сеть", DiagnosticStatus.Passed, "OK", TimeSpan.FromMilliseconds(5)));
        report.Steps.Add(new DiagnosticStepResult("2. DNS", DiagnosticStatus.Passed, "OK", TimeSpan.FromMilliseconds(15)));
        report.Steps.Add(new DiagnosticStepResult("3. TCP 443", DiagnosticStatus.Passed, "RTT: 20ms", TimeSpan.FromMilliseconds(20)));
        report.Steps.Add(new DiagnosticStepResult("4. TLS и ТСПУ", DiagnosticStatus.Passed, "Рукопожатие успешно", TimeSpan.FromMilliseconds(45)));
        report.Steps.Add(new DiagnosticStepResult("5. UDP 443", DiagnosticStatus.Passed, "OK", TimeSpan.FromMilliseconds(50)));

        var text = report.ToFormattedText();

        Assert.Contains("ОБХОДКА: ПОЛНЫЙ ОТЧЁТ ДИАГНОСТИКИ СЕТИ ТЕСТЕРА", text);
        Assert.Contains("Ethernet 1", text);
        Assert.Contains("1492", text);
        Assert.Contains("[PASS]", text);
        Assert.Contains("1. Локальная сеть", text);
        Assert.Contains("HTTP2", text);
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
            udpPort: 443,
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
            udpPort: 443,
            ct: cts.Token);

        Assert.NotNull(report);
        Assert.NotEmpty(report.Steps);
        var formatted = report.ToFormattedText();
        Assert.Contains("ОБХОДКА: ПОЛНЫЙ ОТЧЁТ ДИАГНОСТИКИ СЕТИ ТЕСТЕРА", formatted);
    }
}
