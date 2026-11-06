namespace obxodka.Core.Transports;

//инспектор лучей: мониторинг rtt, детекция блэкхолов и телеметрия
public sealed class RayInspector
{
    //максимальное количество отслеживаемых лучей
    public const int MaxInspectorRays = 3;

    //метки времени последней отправки по лучам
    private readonly long[] _lastTxTicks = new long[MaxInspectorRays];

    //метки времени последнего приема по лучам
    private readonly long[] _lastRxTicks = new long[MaxInspectorRays];

    //сглаженный rtt по лучам в миллисекундах
    private readonly long[] _rayRtt = new long[MaxInspectorRays];

    //счетчики потерянных эхо зондов по лучам
    private readonly int[] _lostProbes = new int[MaxInspectorRays];

    //флаги зависания или блэкхола лучей
    private readonly int[] _isBlackholed = new int[MaxInspectorRays];

    //блокировка истории диагностических шагов
    private readonly Lock _historyLock = new();

    //история диагностических шагов подключения
    private readonly List<string> _history = [];

    //счетчик попыток подключения
    private int _attemptsCount;

    //метка времени последней отправки инцидента
    private long _lastReportTicks;

    //счетчик зависаний из за mtu
    private int _mtuStalls;

    //метка времени последнего приема данных
    private long _lastDataRxTicks;

    //количество отправленных пакетов данных
    private long _dataTxCount;

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

    //текущее количество попыток подключения
    public int AttemptsCount => Volatile.Read(ref _attemptsCount);

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
        if ((uint)ray < MaxInspectorRays)
        {
            Volatile.Write(ref _lastRxTicks[ray], timestamp);
            Volatile.Write(ref _isBlackholed[ray], 0);
            Volatile.Write(ref _lostProbes[ray], 0);
        }
    }

    //фиксация отправки пользовательского пакета данных
    public void RecordDataTx() => Interlocked.Increment(ref _dataTxCount);

    //фиксация приема пользовательского пакета данных
    public void RecordDataRx(int length = 0)
    {
        Volatile.Write(ref _lastDataRxTicks, Stopwatch.GetTimestamp());
        if (length is 0 or >= 20)
        {
            Volatile.Write(ref _mtuStalls, 0);
        }
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
            Volatile.Write(ref _rayRtt[ray], prev <= 0 ? rtt : ((prev * 3) + rtt) / 4);
        }

        Volatile.Write(ref _lastRxTicks[ray], nowTs);
        Volatile.Write(ref _isBlackholed[ray], 0);
        Volatile.Write(ref _lostProbes[ray], 0);
    }

    //фиксация таймаута зонда
    public void RecordProbeTimeout(int ray)
    {
        if ((uint)ray < MaxInspectorRays)
        {
            _ = Interlocked.Increment(ref _lostProbes[ray]);
        }
    }

    //получение rtt луча в миллисекундах
    public long GetRayRtt(int ray) =>
        (uint)ray < MaxInspectorRays ? Volatile.Read(ref _rayRtt[ray]) : 0;

    //получение времени последнего приема по лучу
    public long GetLastRxTicks(int ray) =>
        (uint)ray < MaxInspectorRays ? Volatile.Read(ref _lastRxTicks[ray]) : 0;

    //получение времени последней отправки по лучу
    public long GetLastTxTicks(int ray) =>
        (uint)ray < MaxInspectorRays ? Volatile.Read(ref _lastTxTicks[ray]) : 0;

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

        var tx = Volatile.Read(ref _lastTxTicks[currentPrimaryRay]);
        var rx = Volatile.Read(ref _lastRxTicks[currentPrimaryRay]);
        if (tx <= 0 || rx <= 0)
        {
            return false;
        }

        var txAge = (nowTs - tx) * 1000.0 / Stopwatch.Frequency;
        var rxAge = (nowTs - rx) * 1000.0 / Stopwatch.Frequency;

        if (txAge < 1500 && rxAge > 2000)
        {
            //кандидатом на переключение данных могут быть только рабочие data-лучи ray 0 и ray 1
            for (var candidate = 0; candidate < 2; candidate++)
            {
                if (candidate != currentPrimaryRay && IsRayAlive(candidate, nowTs, 3000))
                {
                    Volatile.Write(ref _isBlackholed[currentPrimaryRay], 1);
                    fallbackRay = candidate;
                    RecordStep($"Asymmetric blackhole on Ray #{currentPrimaryRay} (TX age: {txAge:F0}ms, RX age: {rxAge:F0}ms). Switching to Ray #{candidate}");
                    return true;
                }
            }
        }

        return false;
    }

    //детекция зависания из за превышения mtu канала
    public bool DetectMtuBlackhole(long nowTs)
    {
        var anyRayAlive = IsRayAlive(0, nowTs, 3500) || IsRayAlive(1, nowTs, 3500) || IsRayAlive(2, nowTs, 3500);
        if (!anyRayAlive)
        {
            return false;
        }

        var txCount = Volatile.Read(ref _dataTxCount);
        if (txCount < 6)
        {
            return false;
        }

        var lastDataRx = Volatile.Read(ref _lastDataRxTicks);
        if (lastDataRx == 0)
        {
            var stalls = Interlocked.Increment(ref _mtuStalls);
            return stalls >= 3;
        }

        var silenceData = (nowTs - lastDataRx) * 1000.0 / Stopwatch.Frequency;
        if (silenceData > 3500)
        {
            var stalls = Interlocked.Increment(ref _mtuStalls);
            return stalls >= 3;
        }

        return false;
    }

    //адаптивное ступенчатое понижение mtu
    public bool TryDownshiftMtu(int currentMtu, out int newMtu)
    {
        if (currentMtu > 1360)
        {
            newMtu = 1360;
        }
        else if (currentMtu > 1300)
        {
            newMtu = 1300;
        }
        else if (currentMtu > 1280)
        {
            newMtu = 1280;
        }
        else if (currentMtu > 1240)
        {
            newMtu = 1240;
        }
        else if (currentMtu > 1200)
        {
            newMtu = 1200;
        }
        else
        {
            newMtu = currentMtu;
            return false;
        }

        Volatile.Write(ref _mtuStalls, 0);
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

    //выбор лучей для отправки пакета с учетом здоровья
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SelectRaysForPacket(
        int preferredRay,
        bool needsHedging,
        int activeRays,
        long nowTs,
        out int primaryRay,
        out int duplicateRay)
    {
        _ = activeRays;
        var mask = GetAliveMask(nowTs);
        //пользовательские данные и пинг идут только через ray 0 и ray 1
        PacketRouter.SelectRaysFromHealth(needsHedging, mask, preferredRay, hash: 0, out primaryRay, out duplicateRay);
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
        if (lastReport > 0 && ((nowTicks - lastReport) * 1000.0 / Stopwatch.Frequency) < 300000)
        {
            return false;
        }
        Volatile.Write(ref _lastReportTicks, nowTicks);

        try
        {
            var userEmail = PlatformServices.Preferences.Get(AppPreferenceKeys.UserEmail, "anonymous");
            var hwid = DeviceHelper.Hwid;
            var version = typeof(RayInspector).Assembly.GetName().Version?.ToString() ?? "2.0.0";
            var platform = OperatingSystem.IsWindows() ? "Windows" : (OperatingSystem.IsAndroid() ? "Android" : "Other");

            var report = new IncidentReportDto(
                Email: string.IsNullOrWhiteSpace(userEmail) ? "anonymous" : userEmail,
                Hwid: hwid,
                ClientVersion: version,
                Platform: platform,
                ClientIp: null,
                TargetServerIp: targetIp,
                TargetHost: targetHost,
                EffectivePort: effectivePort,
                Reason: reason,
                DiagnosticSteps: GetDiagnosticSteps(),
                AttemptsCount: Volatile.Read(ref _attemptsCount),
                LastObservedMtu: currentMtu,
                Ray0Rtt: GetRayRtt(0),
                Ray1Rtt: GetRayRtt(1),
                Ray2Rtt: GetRayRtt(2),
                Ray0Blackholed: Volatile.Read(ref _isBlackholed[0]) == 1,
                Ray1Blackholed: Volatile.Read(ref _isBlackholed[1]) == 1,
                Ray2Blackholed: Volatile.Read(ref _isBlackholed[2]) == 1,
                TimestampUtc: DateTime.UtcNow);

            var endpoint = string.IsNullOrWhiteSpace(apiBaseUrl)
                ? AppConfig.ApiUrl("api/vpn/incident")
                : $"{apiBaseUrl.TrimEnd('/')}/api/vpn/incident";

            using var handler = ApiService.CreateDefaultHandler();
            using var httpClient = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(3.5) };
            _ = httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-Device-Hwid", hwid);
            var json = JsonSerializer.Serialize(report, AppJsonContext.Default.IncidentReportDto);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
            AppLogger.LogWarning($"[RAY-INSPECTOR] Telemetry incident report dispatched to {endpoint}: {reason} (Status={response.StatusCode})");
            return response.IsSuccessStatusCode;
        }
        catch
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
        lock (_historyLock)
        {
            _history.Clear();
        }
    }
}
