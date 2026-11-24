using System.ComponentModel;

namespace obxodka.Client.Diagnostics;

//статус выполнения этапа сетевой диагностики
public enum DiagnosticStatus
{
    Passed,
    Failed,
    Warning,
    Skipped
}

//результат отдельного диагностического шага
public sealed record DiagnosticStepResult(
    string StepName,
    DiagnosticStatus Status,
    string Details,
    TimeSpan Duration);

//полный отчет о сетевом окружении и блокировках
public sealed class DiagnosticReport
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string OsInfo { get; init; } = $"{Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})";
    public string PrimaryInterfaceName { get; set; } = "Unknown";
    public int PrimaryInterfaceMtu { get; set; } = 1500;
    public string DefaultGateway { get; set; } = "None";
    public string DetectedManagementSoftware { get; set; } = "None";
    public int TspuHopDistance { get; set; } = -1;
    public string RstAnalysis { get; set; } = "None";
    public string CanaryEchoDetails { get; set; } = string.Empty;
    public List<DiagnosticStepResult> Steps { get; } = [];
    public string RecommendedProtocol { get; set; } = "AUTO";
    public string SummaryVerdict { get; set; } = string.Empty;

    //преобразование отчета в читаемый текст для отправки в поддержку
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
        if (TspuHopDistance > 0)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Дистанция до ТСПУ:     {TspuHopDistance} хопов (TTL-триангуляция)");
        }

        if (!string.IsNullOrEmpty(CanaryEchoDetails))
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Канареечное эхо:       {CanaryEchoDetails}");
        }

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
            _ = sb.AppendLine(CultureInfo.InvariantCulture,
                $"{tag} {step.StepName,-32} ({step.Duration.TotalMilliseconds,5:F0} ms): {step.Details}");
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

//служба комплексной диагностики сетевого подключения и обхода блокировок
public sealed class NetworkDiagnosticsService(HttpClient? httpClient = null)
{
    private readonly HttpClient _httpClient = httpClient ?? new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    //запуск полной цепочки сетевой диагностики
    public async Task<DiagnosticReport> RunFullDiagnosticsAsync(
        string? serverHost = null,
        int serverPort = 443,
        int udpPort = 443,
        Action<DiagnosticStepResult>? onStepCompleted = null,
        CancellationToken ct = default)
    {
        serverHost ??= AppConfig.DirectServerIp;
        var report = new DiagnosticReport();

        await RunStepAsync(report, "1. Локальная сеть и адаптер", () =>
        {
            InspectLocalEnvironment(report);
            return (DiagnosticStatus.Passed,
                $"Интерфейс: {report.PrimaryInterfaceName}, MTU: {report.PrimaryInterfaceMtu}, Gateway: {report.DefaultGateway}");
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
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
            {
                return (DiagnosticStatus.Failed, $"Системный DNS сбоит: {ex.Message}");
            }

            var dohOk = await CheckDohConnectivityAsync(ct).ConfigureAwait(false);
            if (!dohOk)
            {
                return (DiagnosticStatus.Warning,
                    $"Системный DNS OK ({systemAddrs.Length} IP, {systemDnsSw.ElapsedMilliseconds}ms), но DoH (dns.google) заблокирован");
            }

            return (DiagnosticStatus.Passed,
                $"Системный DNS ({systemAddrs.Length} IP, {systemDnsSw.ElapsedMilliseconds}ms) и DoH (dns.google) доступны");
        }, onStepCompleted);

        var tcp443Ok = false;
        await RunStepAsync(report, "3. Доступность TCP 443", async () =>
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            var connectSw = Stopwatch.StartNew();
            try
            {
                await socket.ConnectAsync(serverHost, serverPort, ct).ConfigureAwait(false);
                connectSw.Stop();
                tcp443Ok = true;
                return (DiagnosticStatus.Passed,
                    $"TCP 3-Way Handshake успешен для {serverHost}:{serverPort} (RTT: {connectSw.ElapsedMilliseconds} ms)");
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                return (DiagnosticStatus.Failed, $"ТСПУ сбросил TCP SYN на {serverHost}:{serverPort} (TCP RST)");
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or TimeoutException or ObjectDisposedException
                                           or InvalidOperationException)
            {
                return (DiagnosticStatus.Failed, $"Не удалось подключиться к {serverHost}:{serverPort}: {ex.Message}");
            }
        }, onStepCompleted);

        var directTlsPassed = false;

        if (tcp443Ok)
        {
            await RunStepAsync(report, "4. TLS Handshake", async () =>
            {
                var res = await TestTlsHandshakeAsync(serverHost, serverPort, ct).ConfigureAwait(false);
                directTlsPassed = res.status == DiagnosticStatus.Passed;
                return res;
            }, onStepCompleted);
        }
        else
        {
            report.Steps.Add(new DiagnosticStepResult("4. Проверка TLS и ТСПУ", DiagnosticStatus.Skipped,
                "Пропущен, так как TCP 443 недоступен", TimeSpan.Zero));
        }

        var udp6767Ok = false;
        await RunStepAsync(report, $"5. UDP {udpPort} (UDP Probe)", async () =>
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
            var recommendedMtu = SmartMtuEngine.DefaultMtu;
            if (report.PrimaryInterfaceMtu < 1400)
            {
                recommendedMtu = Math.Min(recommendedMtu, report.PrimaryInterfaceMtu - 80);
            }

            return (DiagnosticStatus.Passed,
                $"Рекомендуемый безопасный MTU туннеля: {recommendedMtu} байт (Физический: {report.PrimaryInterfaceMtu})");
        }, onStepCompleted);

        await RunStepAsync(report, "7. TTL-триангуляция ТСПУ", async () =>
        {
            var (hop, details) =
                await InSituDiagnosticsEngine.MapTspuHopDistanceAsync(serverHost, serverPort, 12, ct).ConfigureAwait(false);
            report.TspuHopDistance = hop;
            var status = hop > 0 ? DiagnosticStatus.Warning : DiagnosticStatus.Passed;
            return (status, details);
        }, onStepCompleted);

        await RunStepAsync(report, "8. Канареечное эхо (Anycast Canary)", async () =>
        {
            var (allPassed, details) = await InSituDiagnosticsEngine.TestCanaryEchoAsync(_httpClient, ct).ConfigureAwait(false);
            report.CanaryEchoDetails = details;
            var status = allPassed ? DiagnosticStatus.Passed : DiagnosticStatus.Warning;
            return (status, details);
        }, onStepCompleted);

        SynthesizeVerdict(report, directTlsPassed, tcp443Ok);
        return report;
    }

    //опрос параметров локального физического сетевого адаптера
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
        catch (Exception ex) when (ex is NetworkInformationException or SocketException or PlatformNotSupportedException)
        {
            report.PrimaryInterfaceName = "Default Physical Interface";
        }

        try
        {
            var mgmtProcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var activeProcs = Process.GetProcesses();
            foreach (var p in activeProcs)
            {
                using (p)
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
                    catch (InvalidOperationException)
                    {
                        //процесс завершился до чтения имени
                    }
                    catch (Win32Exception)
                    {
                        //нет прав на чтение системного процесса
                    }
                    catch (NotSupportedException)
                    {
                        //чтение свойств процесса не поддерживается платформой
                    }
                }
            }

            if (mgmtProcs.Count > 0)
            {
                report.DetectedManagementSoftware = string.Join(", ", mgmtProcs);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException
                                       or PlatformNotSupportedException)
        {
            //подавление ошибки перечисления процессов операционной системы
        }
    }

    //проверка доступности dns через https
    private async Task<bool> CheckDohConnectivityAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://dns.google/resolve?name=google.com&type=A");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or TimeoutException
                                       or OperationCanceledException)
        {
            return false;
        }
    }

    //проверка прохождения рукопожатия tls к серверу
    [SuppressMessage("Security", "CA5359:DoNotDisableCertificateValidation",
        Justification = "Diagnostic probe requires checking TLS handshake independently of certificate trust.")]
    private static async Task<(DiagnosticStatus status, string message)> TestTlsHandshakeAsync(
        string host,
        int port,
        CancellationToken ct)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        try
        {
            await socket.ConnectAsync(host, port, ct).ConfigureAwait(false);
            Stream netStream = new NetworkStream(socket, true);

            using var sslStream = new SslStream(netStream, false, (sender, cert, chain, errors) => true);

            var sslOptions = SslOptionsFactory.CreateStandard(
                "www.microsoft.com",
                [SslApplicationProtocol.Http2]);

            await sslStream.AuthenticateAsClientAsync(sslOptions, ct).ConfigureAwait(false);
            return (DiagnosticStatus.Passed,
                $"Рукопожатие успешно (Протокол: {sslStream.SslProtocol}, ALPN: {sslStream.NegotiatedApplicationProtocol})");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset || ex.ErrorCode == 10054)
        {
            return (DiagnosticStatus.Failed, "ТСПУ оборвал соединение (TCP RST 10054) во время TLS ClientHello");
        }
        catch (IOException ex) when (ex.InnerException is SocketException sex && sex.SocketErrorCode == SocketError.ConnectionReset)
        {
            return (DiagnosticStatus.Failed, "ТСПУ оборвал соединение (TCP RST 10054) во время TLS ClientHello");
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or TimeoutException
                                       or OperationCanceledException or InvalidOperationException)
        {
            return (DiagnosticStatus.Failed, $"Ошибка TLS: {ex.Message}");
        }
    }

    //реальная проверка доступности udp порта с ожиданием ответной датаграммы
    private static async Task<bool> TestUdpReachabilityAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            if (!IPAddress.TryParse(host, out var targetIp))
            {
                var resolved = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                targetIp = resolved.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (targetIp is null)
                {
                    return false;
                }
            }

            using var udp = new Socket(targetIp.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            var ep = new IPEndPoint(targetIp, port);

            //отправляем валидный rfc 5389 stun binding request и ждём реальный ответ сервера
            var stunProbe = new byte[20];
            stunProbe[0] = 0x00;
            stunProbe[1] = 0x01; //Binding Request
            stunProbe[4] = 0x21;
            stunProbe[5] = 0x12;
            stunProbe[6] = 0xA4;
            stunProbe[7] = 0x42; //Magic Cookie
            RandomNumberGenerator.Fill(stunProbe.AsSpan(8, 12));

            _ = await udp.SendToAsync(stunProbe, SocketFlags.None, ep, ct).ConfigureAwait(false);

            using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            recvCts.CancelAfter(TimeSpan.FromMilliseconds(1200));
            var recvBuf = new byte[512];
            EndPoint remoteEp = new IPEndPoint(targetIp.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
            var received = await udp.ReceiveFromAsync(recvBuf, SocketFlags.None, remoteEp, recvCts.Token).ConfigureAwait(false);
            return received.ReceivedBytes > 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or TimeoutException
                                       or OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
    }

    //формирование сводного вердикта на основе проверок
    private static void SynthesizeVerdict(
        DiagnosticReport report,
        bool directTlsPassed,
        bool tcpOk)
    {
        var sb = new StringBuilder();

        if (!tcpOk)
        {
            _ = sb.AppendLine("КРИТИЧЕСКАЯ БЛОКИРОВКА: Провайдер полностью блокирует TCP-подключения к IP сервера (порт 443).");
            _ = sb.AppendLine("Требуется смена IP-адреса ноды либо проверка сетевого подключения.");
            report.RecommendedProtocol = "HTTP2";
        }
        else if (directTlsPassed)
        {
            _ = sb.AppendLine("СЕТЬ ЧИСТАЯ: TLS-рукопожатие проходит без помех со стороны ТСПУ.");
            report.RecommendedProtocol = "HTTP2";
        }
        else
        {
            _ = sb.AppendLine("ВНИМАНИЕ: Блокировка TLS-рукопожатия ТСПУ.");
            _ = sb.AppendLine("Рекомендуется повторное подключение к серверу.");
            report.RecommendedProtocol = "HTTP2";
        }

        if (report.TspuHopDistance > 0)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture,
                $"ТСПУ ЛОКАЛИЗОВАН: Дистанция до инспектора составляет {report.TspuHopDistance} хопов.");
        }

        report.SummaryVerdict = sb.ToString().TrimEnd();
    }

    //выполнение шага диагностики с замером времени
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            status = DiagnosticStatus.Failed;
            message = ex.Message;
        }

        sw.Stop();

        var result = new DiagnosticStepResult(stepName, status, message, sw.Elapsed);
        report.Steps.Add(result);
        onStepCompleted?.Invoke(result);
    }

    //выполнение синхронного шага диагностики
    private static Task RunStepAsync(
        DiagnosticReport report,
        string stepName,
        Func<(DiagnosticStatus status, string message)> action,
        Action<DiagnosticStepResult>? onStepCompleted) =>
        RunStepAsync(report, stepName, () => Task.FromResult(action()), onStepCompleted);
}
