namespace obxodka.Core.Transports;

public sealed class RayInspector
{
    public const int MaxInspectorRays = 3;

    private readonly long[] _lastTxTicks = new long[MaxInspectorRays];
    private readonly long[] _lastRxTicks = new long[MaxInspectorRays];
    private readonly long[] _rayRtt = new long[MaxInspectorRays];
    private readonly int[] _lostProbes = new int[MaxInspectorRays];
    private readonly int[] _isBlackholed = new int[MaxInspectorRays];

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

        if (txAge < 1500 && rxAge > 1400)
        {
            for (var candidate = 0; candidate < MaxInspectorRays; candidate++)
            {
                if (candidate != currentPrimaryRay && IsRayAlive(candidate, nowTs, 3000))
                {
                    Volatile.Write(ref _isBlackholed[currentPrimaryRay], 1);
                    fallbackRay = candidate;
                    return true;
                }
            }
        }

        return false;
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
    }
}
