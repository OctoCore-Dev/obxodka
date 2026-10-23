namespace obxodka.Core.Transports;

public sealed class RayInspector
{
    public const int MaxInspectorRays = 3;

    private readonly long[] _lastTxTicks = new long[MaxInspectorRays];
    private readonly long[] _lastRxTicks = new long[MaxInspectorRays];
    private readonly long[] _rayRtt = new long[MaxInspectorRays];
    private readonly int[] _lostProbes = new int[MaxInspectorRays];
    private readonly int[] _isBlackholed = new int[MaxInspectorRays];

    private readonly Lock _historyLock = new();
    private readonly List<string> _history = [];
    private int _attemptsCount;
    private long _lastReportTicks;
    private int _mtuStalls;
    private long _lastDataRxTicks;
    private long _dataTxCount;

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

    public string GetDiagnosticSteps()
    {
        lock (_historyLock)
        {
            return string.Join(" -> ", _history);
        }
    }

    public void IncrementAttempts() => Interlocked.Increment(ref _attemptsCount);

    public int AttemptsCount => Volatile.Read(ref _attemptsCount);

    public void RecordTx(int ray, long timestamp)
    {
        if ((uint)ray < MaxInspectorRays)
        {
            Volatile.Write(ref _lastTxTicks[ray], timestamp);
        }
    }

    public void RecordRx(int ray, long timestamp)
    {
        if ((uint)ray < MaxInspectorRays)
        {
            Volatile.Write(ref _lastRxTicks[ray], timestamp);
            Volatile.Write(ref _isBlackholed[ray], 0);
            Volatile.Write(ref _lostProbes[ray], 0);
        }
    }

    public void RecordDataTx() => Interlocked.Increment(ref _dataTxCount);

    public void RecordDataRx(int length = 0)
    {
        Volatile.Write(ref _lastDataRxTicks, Stopwatch.GetTimestamp());
        if (length is 0 or >= 20)
        {
            Volatile.Write(ref _mtuStalls, 0);
        }
    }

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

    public void RecordProbeTimeout(int ray)
    {
        if ((uint)ray < MaxInspectorRays)
        {
            _ = Interlocked.Increment(ref _lostProbes[ray]);
        }
    }

    public long GetRayRtt(int ray) =>
        (uint)ray < MaxInspectorRays ? Volatile.Read(ref _rayRtt[ray]) : 0;

    public long GetLastRxTicks(int ray) =>
        (uint)ray < MaxInspectorRays ? Volatile.Read(ref _lastRxTicks[ray]) : 0;

    public long GetLastTxTicks(int ray) =>
        (uint)ray < MaxInspectorRays ? Volatile.Read(ref _lastTxTicks[ray]) : 0;

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
            for (var candidate = 0; candidate < MaxInspectorRays; candidate++)
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

    public void SelectRaysForPacket(
        int preferredRay,
        bool needsHedging,
        int activeRays,
        long nowTs,
        out int primaryRay,
        out int duplicateRay)
    {
        primaryRay = 0;
        duplicateRay = -1;

        var pref = Math.Clamp(preferredRay, 0, Math.Max(1, activeRays - 1));
        if (IsRayAlive(pref, nowTs, 4000))
        {
            primaryRay = pref;
        }
        else
        {
            var found = false;
            for (var i = 0; i < Math.Min(activeRays, MaxInspectorRays); i++)
            {
                if (IsRayAlive(i, nowTs, 4000))
                {
                    primaryRay = i;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                primaryRay = pref;
            }
        }

        if (!needsHedging || activeRays < 2)
        {
            return;
        }

        if (activeRays >= 3 && primaryRay != 2 && IsRayAlive(2, nowTs, 5000))
        {
            duplicateRay = 2;
            return;
        }

        for (var candidate = 0; candidate < Math.Min(activeRays, MaxInspectorRays); candidate++)
        {
            if (candidate != primaryRay && IsRayAlive(candidate, nowTs, 5000))
            {
                duplicateRay = candidate;
                return;
            }
        }

        if (activeRays >= 3 && primaryRay != 2)
        {
            duplicateRay = 2;
        }
        else if (activeRays >= 2)
        {
            duplicateRay = primaryRay == 0 ? 1 : 0;
        }
    }

    public async Task<bool> ReportIncidentIfExhaustedAsync(
        string reason,
        string targetHost,
        int effectivePort,
        int currentMtu,
        string? targetIp = null,
        string? apiBaseUrl = null,
        CancellationToken cancellationToken = default)
    {
        var nowTicks = Stopwatch.GetTimestamp();
        var lastReport = Volatile.Read(ref _lastReportTicks);
        if (lastReport > 0 && ((nowTicks - lastReport) * 1000.0 / Stopwatch.Frequency) < 300000)
        {
            return false;
        }
        Volatile.Write(ref _lastReportTicks, nowTicks);

        try
        {
            var userEmail = Preferences.Default.Get("user_email", "anonymous");
            var hwid = DeviceHelper.GetHwid();
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

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3.5) };
            var json = JsonSerializer.Serialize(report, AppJsonContext.Default.IncidentReportDto);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
            Shared.Logging.AppLogger.LogWarning($"[RAY-INSPECTOR] Telemetry incident report dispatched to {endpoint}: {reason} (Status={response.StatusCode})");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

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
