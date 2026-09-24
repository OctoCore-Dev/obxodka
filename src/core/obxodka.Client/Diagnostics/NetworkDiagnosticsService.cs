namespace obxodka.Client.Diagnostics;

public enum DiagnosticStatus
{
    Passed,
    Failed,
    Warning,
    Skipped
}

public sealed record DiagnosticStepResult(
    string StepName,
    DiagnosticStatus Status,
    string Details,
    TimeSpan Duration);

public sealed class DiagnosticReport
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string OsInfo { get; init; } = $"{Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})";
    public string PrimaryInterfaceName { get; set; } = "Unknown";
    public int PrimaryInterfaceMtu { get; set; } = 1500;
    public string DefaultGateway { get; set; } = "None";
    public string DetectedManagementSoftware { get; set; } = "None";
    public List<DiagnosticStepResult> Steps { get; } = [];
    public string RecommendedProtocol { get; set; } = "AUTO";
    public string SummaryVerdict { get; set; } = string.Empty;

    public string ToFormattedText()
    {
        var sb = new StringBuilder();
        _ = sb.AppendLine("================================================================================");
        _ = sb.AppendLine("                 ОБХОДКА: ПОЛНЫЙ ОТЧЁТ ДИАГНОСТИКИ СЕТИ ТЕСТЕРА                 ");
        _ = sb.AppendLine("================================================================================");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Дата и время:          {Timestamp:yyyy-MM-dd HH:mm:ss} UTC");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Операционная система:  {OsInfo}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Основной сетевой адаптер: {PrimaryInterfaceName} (MTU: {PrimaryInterfaceMtu})");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Шлюз по умолчанию:     {DefaultGateway}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Удалённый доступ:      {DetectedManagementSoftware}");
        _ = sb.AppendLine("--------------------------------------------------------------------------------");
        _ = sb.AppendLine("ЭТАПЫ ПРОВЕРКИ:");
        _ = sb.AppendLine("--------------------------------------------------------------------------------");

        foreach (var step in Steps)
        {
            var tag = step.Status switch
            {
                DiagnosticStatus.Passed => "[PASS]   ",
                DiagnosticStatus.Failed => "[BLOCKED]",
                DiagnosticStatus.Warning => "[WARN]   ",
                DiagnosticStatus.Skipped => "[SKIP]   ",
                _ => "[INFO]   "
            };
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{tag} {step.StepName,-32} ({step.Duration.TotalMilliseconds,5:F0} ms): {step.Details}");
        }

        _ = sb.AppendLine("--------------------------------------------------------------------------------");
        _ = sb.AppendLine("ИТОГОВЫЙ ВЕРДИКТ:");
        _ = sb.AppendLine(SummaryVerdict);
        _ = sb.AppendLine("--------------------------------------------------------------------------------");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"РЕКОМЕНДУЕМЫЙ ПРОТОКОЛ: {RecommendedProtocol}");
        _ = sb.AppendLine("================================================================================");
        return sb.ToString();
    }
}

public sealed class NetworkDiagnosticsService(HttpClient? httpClient = null)
{
    private readonly HttpClient _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

    public async Task<DiagnosticReport> RunFullDiagnosticsAsync(
        string serverHost = "45.63.117.29",
        int serverPort = 443,
        int udpPort = FechsueTransport.FechsueServerPort,
        Action<DiagnosticStepResult>? onStepCompleted = null,
        CancellationToken ct = default)
    {
        var report = new DiagnosticReport();

        await RunStepAsync(report, "1. Локальная сеть и адаптер", () =>
        {
            InspectLocalEnvironment(report);
            return (DiagnosticStatus.Passed, $"Интерфейс: {report.PrimaryInterfaceName}, MTU: {report.PrimaryInterfaceMtu}, Gateway: {report.DefaultGateway}");
        }, onStepCompleted);

        await RunStepAsync(report, "2. Системный DNS и DoH", async () =>
        {
            var systemDnsSw = Stopwatch.StartNew();
            IPAddress[] systemAddrs;
            try
            {
                systemAddrs = await Dns.GetHostAddressesAsync("cloudflare.com", ct).ConfigureAwait(false);
                systemDnsSw.Stop();
            }
            catch (Exception ex)
            {
                return (DiagnosticStatus.Failed, $"Системный DNS сбоит: {ex.Message}");
            }

            var dohOk = await CheckDohConnectivityAsync(ct).ConfigureAwait(false);
            if (!dohOk)
            {
                return (DiagnosticStatus.Warning, $"Системный DNS OK ({systemAddrs.Length} IP, {systemDnsSw.ElapsedMilliseconds}ms), но DoH (dns.google) заблокирован");
            }

            return (DiagnosticStatus.Passed, $"Системный DNS ({systemAddrs.Length} IP, {systemDnsSw.ElapsedMilliseconds}ms) и DoH (dns.google) доступны");
        }, onStepCompleted);

        var tcp443Ok = false;
        await RunStepAsync(report, "3. Доступность TCP 443", async () =>
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            var connectSw = Stopwatch.StartNew();
            try
            {
                await socket.ConnectAsync(serverHost, serverPort, ct).ConfigureAwait(false);
                connectSw.Stop();
                tcp443Ok = true;
                return (DiagnosticStatus.Passed, $"TCP 3-Way Handshake успешен для {serverHost}:{serverPort} (RTT: {connectSw.ElapsedMilliseconds} ms)");
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                return (DiagnosticStatus.Failed, $"ТСПУ сбросил TCP SYN на {serverHost}:{serverPort} (TCP RST)");
            }
            catch (Exception ex)
            {
                return (DiagnosticStatus.Failed, $"Не удалось подключиться к {serverHost}:{serverPort}: {ex.Message}");
            }
        }, onStepCompleted);

        var directTlsPassed = false;
        var splitTlsPassed = false;

        if (tcp443Ok)
        {
            await RunStepAsync(report, "4A. TLS Handshake (Прямой)", async () =>
            {
                var res = await TestTlsHandshakeAsync(serverHost, serverPort, useDpiBypass: false, ct).ConfigureAwait(false);
                directTlsPassed = res.status == DiagnosticStatus.Passed;
                return res;
            }, onStepCompleted);

            await RunStepAsync(report, "4B. TLS с DpiBypassStream", async () =>
            {
                var res = await TestTlsHandshakeAsync(serverHost, serverPort, useDpiBypass: true, ct).ConfigureAwait(false);
                splitTlsPassed = res.status == DiagnosticStatus.Passed;
                return res;
            }, onStepCompleted);
        }
        else
        {
            report.Steps.Add(new DiagnosticStepResult("4. Проверка TLS и ТСПУ", DiagnosticStatus.Skipped, "Пропущен, так как TCP 443 недоступен", TimeSpan.Zero));
        }

        var udp6767Ok = false;
        await RunStepAsync(report, $"5. UDP {udpPort} (FECHSUE)", async () =>
        {
            udp6767Ok = await TestUdpReachabilityAsync(serverHost, udpPort, ct).ConfigureAwait(false);
            if (udp6767Ok)
            {
                return (DiagnosticStatus.Passed, $"Порт {serverHost}:{udpPort} UDP отвечает");
            }
            return (DiagnosticStatus.Failed, $"Порт {serverHost}:{udpPort} UDP заблокирован или сброшен провайдером (Таймаут)");
        }, onStepCompleted);

        await RunStepAsync(report, "6. Определение безопасного MTU", () =>
        {
            var recommendedMtu = 1280;
            if (report.PrimaryInterfaceMtu < 1400)
            {
                recommendedMtu = Math.Min(recommendedMtu, report.PrimaryInterfaceMtu - 80);
            }
            return (DiagnosticStatus.Passed, $"Рекомендуемый безопасный MTU туннеля: {recommendedMtu} байт (Физический: {report.PrimaryInterfaceMtu})");
        }, onStepCompleted);

        SynthesizeVerdict(report, directTlsPassed, splitTlsPassed, udp6767Ok, tcp443Ok);
        return report;
    }

    private static void InspectLocalEnvironment(DiagnosticReport report)
    {
        try
        {
            var activeNic = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                            !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                            !n.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(n => n.Speed)
                .FirstOrDefault();

            if (activeNic != null)
            {
                report.PrimaryInterfaceName = activeNic.Name;
                var ipProps = activeNic.GetIPProperties();
                var ipv4Props = ipProps.GetIPv4Properties();
                if (ipv4Props != null)
                {
                    report.PrimaryInterfaceMtu = ipv4Props.Mtu;
                }

                var gw = ipProps.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                if (gw != null)
                {
                    report.DefaultGateway = gw.Address.ToString();
                }
            }
        }
        catch
        {
            report.PrimaryInterfaceName = "Default Physical Interface";
        }

        try
        {
            var mgmtProcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var activeProcs = Process.GetProcesses();
            foreach (var p in activeProcs)
            {
                try
                {
                    var name = p.ProcessName;
                    if (name.Contains("AnyDesk", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("TeamViewer", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("RustDesk", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = mgmtProcs.Add($"{name} (PID: {p.Id})");
                    }
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }

            if (mgmtProcs.Count > 0)
            {
                report.DetectedManagementSoftware = string.Join(", ", mgmtProcs);
            }
        }
        catch { }
    }

    private async Task<bool> CheckDohConnectivityAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://dns.google/resolve?name=google.com&type=A");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    [SuppressMessage("Security", "CA5359:DoNotDisableCertificateValidation", Justification = "Diagnostic probe requires checking TLS handshake independently of certificate trust.")]
    private static async Task<(DiagnosticStatus status, string message)> TestTlsHandshakeAsync(
        string host,
        int port,
        bool useDpiBypass,
        CancellationToken ct)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(host, port, ct).ConfigureAwait(false);
            Stream netStream = new NetworkStream(socket, ownsSocket: true);
            if (useDpiBypass)
            {
                netStream = new DpiBypassStream(netStream, splitPosition: 2, delayMs: 25);
            }

            using var sslStream = new SslStream(netStream, leaveInnerStreamOpen: false, (sender, cert, chain, errors) => true);

            var sslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "www.microsoft.com",
                ApplicationProtocols = [SslApplicationProtocol.Http2],
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13 | System.Security.Authentication.SslProtocols.Tls12
            };

            await sslStream.AuthenticateAsClientAsync(sslOptions, ct).ConfigureAwait(false);
            return (DiagnosticStatus.Passed, $"Рукопожатие успешно (Протокол: {sslStream.SslProtocol}, ALPN: {sslStream.NegotiatedApplicationProtocol})");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset || ex.ErrorCode == 10054)
        {
            return (DiagnosticStatus.Failed, "ТСПУ оборвал соединение (TCP RST 10054) во время TLS ClientHello");
        }
        catch (IOException ex) when (ex.InnerException is SocketException sex && sex.SocketErrorCode == SocketError.ConnectionReset)
        {
            return (DiagnosticStatus.Failed, "ТСПУ оборвал соединение (TCP RST 10054) во время TLS ClientHello");
        }
        catch (Exception ex)
        {
            return (DiagnosticStatus.Failed, $"Ошибка TLS: {ex.Message}");
        }
    }

    private static async Task<bool> TestUdpReachabilityAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var udp = new Socket(SocketType.Dgram, ProtocolType.Udp);
            udp.ReceiveTimeout = 1500;
            var ep = new IPEndPoint(IPAddress.Parse(host), port);

            var dummyPacket = new byte[32];
            Random.Shared.NextBytes(dummyPacket);

            _ = await udp.SendToAsync(dummyPacket, SocketFlags.None, ep, ct).ConfigureAwait(false);

            await Task.Delay(300, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void SynthesizeVerdict(
        DiagnosticReport report,
        bool directTlsPassed,
        bool splitTlsPassed,
        bool udpOk,
        bool tcpOk)
    {
        var sb = new StringBuilder();

        if (!tcpOk)
        {
            _ = sb.AppendLine("КРИТИЧЕСКАЯ БЛОКИРОВКА: Провайдер полностью блокирует TCP-подключения к IP сервера (порт 443).");
            _ = sb.AppendLine("Требуется смена IP-адреса ноды либо использование Mesh/CDN Relay.");
            report.RecommendedProtocol = "MESH_RELAY";
        }
        else if (!directTlsPassed && splitTlsPassed)
        {
            _ = sb.AppendLine("ОБНАРУЖЕН МОСКОВСКИЙ ТСПУ: Прямой TLS сбрасывается по признаку несоответствия SNI/IP.");
            _ = sb.AppendLine("МЕХАНИЗМ ОБХОДА СРАБОТАЛ: Сплиттинг ClientHello (DpiBypassStream) успешно преодолел фильтр!");
            _ = sb.AppendLine("Рекомендуется работать по протоколу HTTP/2 с активным DpiBypassStream.");
            report.RecommendedProtocol = "HTTP2 (DpiBypass)";
        }
        else if (directTlsPassed)
        {
            _ = sb.AppendLine("СЕТЬ ЧИСТАЯ: TLS-рукопожатие проходит без помех со стороны ТСПУ.");
            report.RecommendedProtocol = udpOk ? "FECHSUE (UDP)" : "HTTP2";
        }
        else
        {
            _ = sb.AppendLine("ВНИМАНИЕ: Блокировка как прямого, так и расщепленного TLS.");
            _ = sb.AppendLine("Рекомендуется переключение на Mesh Relay или Cloudflare Bridge.");
            report.RecommendedProtocol = "MESH_RELAY / BRIDGE";
        }

        report.SummaryVerdict = sb.ToString().TrimEnd();
    }

    private static async Task RunStepAsync(
        DiagnosticReport report,
        string stepName,
        Func<Task<(DiagnosticStatus status, string message)>> action,
        Action<DiagnosticStepResult>? onStepCompleted)
    {
        var sw = Stopwatch.StartNew();
        DiagnosticStatus status;
        string message;
        try
        {
            (status, message) = await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            status = DiagnosticStatus.Failed;
            message = ex.Message;
        }
        sw.Stop();

        var result = new DiagnosticStepResult(stepName, status, message, sw.Elapsed);
        report.Steps.Add(result);
        onStepCompleted?.Invoke(result);
    }

    private static Task RunStepAsync(
        DiagnosticReport report,
        string stepName,
        Func<(DiagnosticStatus status, string message)> action,
        Action<DiagnosticStepResult>? onStepCompleted) =>
        RunStepAsync(report, stepName, () => Task.FromResult(action()), onStepCompleted);
}

