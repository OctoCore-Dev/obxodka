namespace obxodka.Core.Transports;

//инспектор лучей: мониторинг rtt, детекция блэкхолов и телеметрия
public sealed class RayInspector
{
    //максимальное количество отслеживаемых лучей
    public const int MaxInspectorRays = 3;

    //история диагностических шагов подключения
    private readonly List<string> _history = [];

    //блокировка истории диагностических шагов
    private readonly Lock _historyLock = new();

    //флаги зависания или блэкхола лучей
    private readonly int[] _isBlackholed = new int[MaxInspectorRays];

    //метки времени последнего приема по лучам
    private readonly long[] _lastRxTicks = new long[MaxInspectorRays];

    //метки времени последней отправки по лучам
    private readonly long[] _lastTxTicks = new long[MaxInspectorRays];

    //счетчики потерянных эхо зондов по лучам
    private readonly int[] _lostProbes = new int[MaxInspectorRays];

    //сглаженный rtt по лучам в миллисекундах
    private readonly long[] _rayRtt = new long[MaxInspectorRays];

    //счетчик попыток подключения
    private int _attemptsCount;

    //базовый срез отправленных пакетов для оконного расчета mtu блэкхола
    private long _baselineDataTxCount;

    //количество отправленных крупных юникаст пакетов данных
    private long _dataTxCount;

    //метка времени последнего приема данных
    private long _lastDataRxTicks;

    //метка времени последней миграции луча для защиты от дребезга
    private long _lastMigrationTicks;

    //метка времени последней записи о минимальном mtu
    private long _lastMinMtuStepTicks;

    //метка времени последней отправки инцидента
    private long _lastReportTicks;

    //счетчик зависаний из за mtu
    private int _mtuStalls;

    //текущее количество попыток подключения
    public int AttemptsCount => Volatile.Read(ref _attemptsCount);

    //получение rtt луча в миллисекундах
    public long GetRayRtt(int ray) =>
        (uint)ray < MaxInspectorRays ? Volatile.Read(ref _rayRtt[ray]) : 0;

    //получение времени последнего приема по лучу
    public long GetLastRxTicks(int ray) =>
        (uint)ray < MaxInspectorRays ? Volatile.Read(ref _lastRxTicks[ray]) : 0;

    //получение времени последней отправки по лучу
    public long GetLastTxTicks(int ray) =>
        (uint)ray < MaxInspectorRays ? Volatile.Read(ref _lastTxTicks[ray]) : 0;

    //запись шага в историю диагностики
    public void RecordStep(string step)
    {
        lock (_historyLock)
        {
            if (_history.Count >= 30)
            {
                _history.RemoveAt(0);
            }

            var ts = DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            _history.Add($"[{ts}] {step}");
        }
    }

    //получение цепочки шагов диагностики
    public string GetDiagnosticSteps()
    {
        lock (_historyLock)
        {
            return string.Join(" -> ", _history);
        }
    }

    //увеличение счетчика попыток подключения
    public void IncrementAttempts() => Interlocked.Increment(ref _attemptsCount);

    //фиксация отправки пакета по лучу
    public void RecordTx(int ray, long timestamp)
    {
        if ((uint)ray < MaxInspectorRays)
        {
            Volatile.Write(ref _lastTxTicks[ray], timestamp);
        }
    }

    //фиксация приема пакета по лучу
    public void RecordRx(int ray, long timestamp)
    {
        if ((uint)ray >= MaxInspectorRays)
        {
            return;
        }

        Volatile.Write(ref _lastRxTicks[ray], timestamp);
        Volatile.Write(ref _isBlackholed[ray], 0);
        Volatile.Write(ref _lostProbes[ray], 0);
    }

    //фиксация отправки пользовательского пакета данных
    public void RecordDataTx() => Interlocked.Increment(ref _dataTxCount);

    //фиксация отправки только крупных юникаст пакетов способных вызвать mtu блэкхол
    public void RecordDataTx(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 600)
        {
            return;
        }

        var version = packet[0] >> 4;
        switch (version)
        {
            case 4 when packet.Length < 20:
                return;
            case 4:
                {
                    var dst0 = packet[16];
                    var dst1 = packet[17];
                    var dst3 = packet[19];
                    //игнорируем мультикаст бродкаст и link-local пакеты на которые сервер не отвечает
                    if (dst0 >= 224 || dst3 == 255 || dst0 == 169 && dst1 == 254)
                    {
                        return;
                    }

                    break;
                }
            case 6 when packet.Length < 40:
                return;
            case 6:
                {
                    var dst0 = packet[24];
                    var dst1 = packet[25];
                    if (dst0 == 0xFF || dst0 == 0xFE && (dst1 & 0xC0) == 0x80)
                    {
                        return;
                    }

                    break;
                }
            default:
                return;
        }

        _ = Interlocked.Increment(ref _dataTxCount);
    }

    //фиксация приема пользовательского пакета данных
    public void RecordDataRx(int length = 0)
    {
        Volatile.Write(ref _lastDataRxTicks, Stopwatch.GetTimestamp());
        if (length is not (0 or >= 20))
        {
            return;
        }

        Volatile.Write(ref _mtuStalls, 0);
        Volatile.Write(ref _baselineDataTxCount, Volatile.Read(ref _dataTxCount));
    }

    //фиксация ответа на пинг зонд и расчет rtt
    public void RecordPong(int ray, long sendTs, long nowTs)
    {
        if ((uint)ray >= MaxInspectorRays)
        {
            return;
        }

        var rtt = (long)((nowTs - sendTs) * 1000.0 / Stopwatch.Frequency);
        if (rtt is >= 0 and < 10000)
        {
            var prev = Volatile.Read(ref _rayRtt[ray]);
            Volatile.Write(ref _rayRtt[ray], prev <= 0 ? rtt : (prev * 3 + rtt) / 4);
        }

        Volatile.Write(ref _lastRxTicks[ray], nowTs);
        Volatile.Write(ref _isBlackholed[ray], 0);
        Volatile.Write(ref _lostProbes[ray], 0);
    }

    //фиксация таймаута зонда
    public void RecordProbeTimeout(int ray)
    {
        if ((uint)ray < MaxInspectorRays && Interlocked.Increment(ref _lostProbes[ray]) >= 3)
        {
            Volatile.Write(ref _isBlackholed[ray], 1);
        }
    }

    //проверка живости луча по времени тишины
    public bool IsRayAlive(int ray, long nowTs, double maxSilenceMs = 6000)
    {
        if ((uint)ray >= MaxInspectorRays)
        {
            return false;
        }

        var rx = Volatile.Read(ref _lastRxTicks[ray]);
        if (rx <= 0)
        {
            return false;
        }

        var silence = (nowTs - rx) * 1000.0 / Stopwatch.Frequency;
        return silence < maxSilenceMs && Volatile.Read(ref _isBlackholed[ray]) == 0;
    }

    //детекция односторонней блокировки канала (tx идет, rx завис)
    public bool DetectAsymmetricBlackhole(int currentPrimaryRay, long nowTs, out int fallbackRay)
    {
        fallbackRay = -1;
        if ((uint)currentPrimaryRay >= MaxInspectorRays)
        {
            return false;
        }

        var lastMigration = Volatile.Read(ref _lastMigrationTicks);
        if (lastMigration > 0 && (nowTs - lastMigration) * 1000.0 / Stopwatch.Frequency < 5000)
        {
            return false;
        }

        var tx = Volatile.Read(ref _lastTxTicks[currentPrimaryRay]);
        var rx = Volatile.Read(ref _lastRxTicks[currentPrimaryRay]);
        if (tx <= 0 || rx <= 0)
        {
            return false;
        }

        var txAge = (nowTs - tx) * 1000.0 / Stopwatch.Frequency;
        var rxAge = (nowTs - rx) * 1000.0 / Stopwatch.Frequency;
        var rtt = GetRayRtt(currentPrimaryRay);
        var minTxWaitMs = Math.Max(150.0, rtt * 2.0);
        var minRxSilenceMs = Math.Max(2000.0, rtt * 4.0);

        if (txAge < minTxWaitMs || txAge >= 1500 || rxAge <= minRxSilenceMs)
        {
            return false;
        }

        //кандидатом на переключение данных могут быть только рабочие data-лучи ray 0 и ray 1
        for (var candidate = 0; candidate < 2; candidate++)
        {
            if (candidate == currentPrimaryRay || !IsRayAlive(candidate, nowTs, 3000))
            {
                continue;
            }

            Volatile.Write(ref _isBlackholed[currentPrimaryRay], 1);
            Volatile.Write(ref _lastMigrationTicks, nowTs);
            fallbackRay = candidate;
            RecordStep(
                $"Asymmetric blackhole on Ray #{currentPrimaryRay} (TX age: {txAge:F0}ms, RX age: {rxAge:F0}ms). Switching to Ray #{candidate}");
            return true;
        }

        return false;
    }

    //детекция зависания из за превышения mtu канала по дельте новых крупных пакетов
    public bool DetectMtuBlackhole(long nowTs)
    {
        var anyRayAlive = IsRayAlive(0, nowTs, 3500) || IsRayAlive(1, nowTs, 3500) || IsRayAlive(2, nowTs, 3500);
        if (!anyRayAlive)
        {
            return false;
        }

        var txCount = Volatile.Read(ref _dataTxCount);
        var baselineTx = Volatile.Read(ref _baselineDataTxCount);
        if (txCount - baselineTx < 6)
        {
            return false;
        }

        var lastDataRx = Volatile.Read(ref _lastDataRxTicks);
        if (lastDataRx == 0)
        {
            var stalls = Interlocked.Increment(ref _mtuStalls);
            if (stalls < 3)
            {
                return false;
            }

            Volatile.Write(ref _mtuStalls, 0);
            Volatile.Write(ref _baselineDataTxCount, txCount);
            return true;
        }

        var silenceData = (nowTs - lastDataRx) * 1000.0 / Stopwatch.Frequency;
        if (silenceData <= 3500)
        {
            return false;
        }

        {
            var stalls = Interlocked.Increment(ref _mtuStalls);
            if (stalls < 3)
            {
                return false;
            }

            Volatile.Write(ref _mtuStalls, 0);
            Volatile.Write(ref _baselineDataTxCount, txCount);
            return true;
        }
    }

    //троттлинг записи о сохранении блэкхола на минимальном mtu
    public void TryRecordMinMtuPersistence(int currentMtu, long nowTs)
    {
        var prev = Volatile.Read(ref _lastMinMtuStepTicks);
        if (prev > 0 && (nowTs - prev) * 1000.0 / Stopwatch.Frequency < 60000)
        {
            return;
        }

        Volatile.Write(ref _lastMinMtuStepTicks, nowTs);
        RecordStep($"MTU blackhole persisted at minimum MTU {currentMtu}");
    }

    //адаптивное ступенчатое понижение mtu
    public bool TryDownshiftMtu(int currentMtu, out int newMtu)
    {
        switch (currentMtu)
        {
            case > 1360:
                newMtu = 1360;
                break;
            case > 1300:
                newMtu = 1300;
                break;
            case > 1280:
                newMtu = 1280;
                break;
            case > 1240:
                newMtu = 1240;
                break;
            case > 1200:
                newMtu = 1200;
                break;
            default:
                newMtu = currentMtu;
                return false;
        }

        Volatile.Write(ref _mtuStalls, 0);
        Volatile.Write(ref _baselineDataTxCount, Volatile.Read(ref _dataTxCount));
        RecordStep($"Adaptive MTU downshifted: {currentMtu} -> {newMtu}");
        return true;
    }

    //маска активных и живых лучей
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetAliveMask(long nowTs, double maxSilenceMs = 4000)
    {
        byte mask = 0;
        if (IsRayAlive(0, nowTs, maxSilenceMs))
        {
            mask |= PacketRouter.Ray0Mask;
        }

        if (IsRayAlive(1, nowTs, maxSilenceMs))
        {
            mask |= PacketRouter.Ray1Mask;
        }

        if (IsRayAlive(2, nowTs, maxSilenceMs))
        {
            mask |= PacketRouter.Ray2Mask;
        }

        return mask;
    }

    //отправка телеметрического отчета об инциденте на сервер
    public async Task<bool> ReportIncidentIfExhaustedAsync(
        string reason,
        string targetHost,
        int effectivePort,
        int currentMtu,
        string? targetIp = null,
        string? apiBaseUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (!PlatformServices.Preferences.Get(AppPreferenceKeys.UseTelemetry, true))
        {
            return false;
        }

        var nowTicks = Stopwatch.GetTimestamp();
        var lastReport = Volatile.Read(ref _lastReportTicks);
        if (lastReport > 0 && (nowTicks - lastReport) * 1000.0 / Stopwatch.Frequency < 300000)
        {
            return false;
        }

        Volatile.Write(ref _lastReportTicks, nowTicks);

        try
        {
            var userEmail = PlatformServices.Preferences.Get(AppPreferenceKeys.UserEmail, "anonymous");
            var hwid = DeviceHelper.Hwid;
            var version = typeof(RayInspector).Assembly.GetName().Version?.ToString() ?? "2.0.0";
            var platform = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsAndroid() ? "Android" : "Other";

            var report = new IncidentReportDto(
                string.IsNullOrWhiteSpace(userEmail) ? "anonymous" : userEmail,
                hwid,
                version,
                platform,
                null,
                targetIp,
                targetHost,
                effectivePort,
                reason,
                GetDiagnosticSteps(),
                Volatile.Read(ref _attemptsCount),
                currentMtu,
                GetRayRtt(0),
                GetRayRtt(1),
                GetRayRtt(2),
                Volatile.Read(ref _isBlackholed[0]) == 1,
                Volatile.Read(ref _isBlackholed[1]) == 1,
                Volatile.Read(ref _isBlackholed[2]) == 1,
                DateTime.UtcNow);

            var useDefaultApiPipeline = string.IsNullOrWhiteSpace(apiBaseUrl) ||
                                        string.Equals(apiBaseUrl.TrimEnd('/'), AppConfig.ApiBaseUrl.TrimEnd('/'),
                                            StringComparison.OrdinalIgnoreCase);

            var endpoint = useDefaultApiPipeline
                ? AppConfig.ApiUrl("api/vpn/incident")
                : $"{apiBaseUrl!.TrimEnd('/')}/api/vpn/incident";

            //блокируем отправку тестовых инцидентов на боевой сервер при прогоне юнит тестов
            if (TelemetryFilter.IsTestEnvironment &&
                !endpoint.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                !endpoint.Contains("localhost", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (useDefaultApiPipeline)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(6));
                var sent = await ApiService.Shared.PostIncidentAsync(report, timeoutCts.Token).ConfigureAwait(false);
                AppLogger.LogWarning($"[RAY-INSPECTOR] Telemetry incident report dispatched via ApiService: {reason} (Success={sent})");
                return sent;
            }

            using var handler = ApiService.CreateDefaultHandler();
            using var httpClient = new HttpClient(handler, true);
            httpClient.Timeout = TimeSpan.FromSeconds(3.5);
            _ = httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-Device-Hwid", hwid);
            _ = httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-Telemetry-Token", AppConfig.TelemetryAuthToken);
            var json = JsonSerializer.Serialize(report, AppJsonContext.Default.IncidentReportDto);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
            AppLogger.LogWarning(
                $"[RAY-INSPECTOR] Telemetry incident report dispatched to {endpoint}: {reason} (Status={response.StatusCode})");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or SocketException
                                       or JsonException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    //сброс состояния инспектора
    public void Reset()
    {
        for (var i = 0; i < MaxInspectorRays; i++)
        {
            Volatile.Write(ref _lastTxTicks[i], 0);
            Volatile.Write(ref _lastRxTicks[i], 0);
            Volatile.Write(ref _rayRtt[i], 0);
            Volatile.Write(ref _lostProbes[i], 0);
            Volatile.Write(ref _isBlackholed[i], 0);
        }

        Volatile.Write(ref _mtuStalls, 0);
        Volatile.Write(ref _lastDataRxTicks, 0);
        Volatile.Write(ref _dataTxCount, 0);
        Volatile.Write(ref _baselineDataTxCount, 0);
        Volatile.Write(ref _lastMigrationTicks, 0);
        Volatile.Write(ref _lastMinMtuStepTicks, 0);
        lock (_historyLock)
        {
            _history.Clear();
        }
    }
}
