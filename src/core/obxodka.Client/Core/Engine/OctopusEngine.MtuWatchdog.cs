namespace obxodka.Core;

public sealed partial class OctopusEngine
{
    private long _watchdogArmedTicks;
    private CancellationTokenSource? _mtuScanCts;
    private int _isScanningMtu;

    //взвод сторожевого таймера контроля зависания трафика
    public void ArmTrafficWatchdog()
    {
        Volatile.Write(ref _watchdogArmedTicks, Environment.TickCount64);
        ScheduleSmartMtuScan(2500);
    }

    //планирование отложенного интеллектуального замера mtu
    public void ScheduleSmartMtuScan(int delayMs = 2500)
    {
        try
        {
            _mtuScanCts?.Cancel();
            _mtuScanCts?.Dispose();
        }
        catch { }

        var engineCts = _cts;
        if (engineCts is null || engineCts.IsCancellationRequested)
        {
            return;
        }

        var scanCts = CancellationTokenSource.CreateLinkedTokenSource(engineCts.Token);
        _mtuScanCts = scanCts;
        var token = scanCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                }

                if (!token.IsCancellationRequested && IsConnected)
                {
                    _ = await ScanAndLockOptimalMtuAsync(token).ConfigureAwait(false);
                }
            }
            catch { }
        }, token);
    }

    //двунаправленное зондирование (downlink burst + uplink probe) для нахождения и фиксации оптимального mtu
    public async Task<int> ScanAndLockOptimalMtuAsync(CancellationToken ct = default)
    {
        var transport = _transport;
        if (!IsConnected || transport is null)
        {
            return SmartMtuEngine.CurrentMtu;
        }

        if (Interlocked.CompareExchange(ref _isScanningMtu, 1, 0) != 0)
        {
            return SmartMtuEngine.CurrentMtu;
        }

        try
        {
            EmitStatus(VpnStatusMessages.ScanningMtuHole(SmartMtuEngine.MaxMtu, SmartMtuEngine.MinMtu, 8));
            var burstResults = await transport.MeasureBurstMtuAsync(ct).ConfigureAwait(false);
            var optimalMtu = SmartMtuEngine.DefaultMtu;

            if (burstResults.Length > 0)
            {
                var downlinkMax = Math.Clamp(burstResults.Max(), SmartMtuEngine.MinMtu, SmartMtuEngine.MaxMtu);
                optimalMtu = downlinkMax;

                //проверяем проходимость найденного кандидата и в обратном направлении (uplink probe)
                var steps = SmartMtuEngine.MtuEscalationSteps;
                for (var i = steps.Length - 1; i >= 0; i--)
                {
                    var candidate = steps[i];
                    if (candidate > downlinkMax)
                    {
                        continue;
                    }

                    if (await transport.ProbeMtuAsync(candidate, ct).ConfigureAwait(false))
                    {
                        optimalMtu = candidate;
                        break;
                    }
                }

                EmitStatus(VpnStatusMessages.MtuHoleFound(optimalMtu));
            }
            else
            {
                optimalMtu = await transport.ProbeMtuAsync(SmartMtuEngine.DefaultMtu, ct).ConfigureAwait(false)
                    ? SmartMtuEngine.DefaultMtu
                    : SmartMtuEngine.MinMtu;
            }

            SmartMtuEngine.CurrentMtu = optimalMtu;
            EmitStatus(VpnStatusMessages.MtuSyncingWithServer(SmartMtuEngine.CurrentMtu));
            await (_transport?.SendMtuSyncAsync(optimalMtu) ?? Task.CompletedTask).ConfigureAwait(false);
            OnMtuUpdated?.Invoke(optimalMtu);
            EmitStatus(VpnStatusMessages.MtuLocked(SmartMtuEngine.CurrentMtu));
            return SmartMtuEngine.CurrentMtu;
        }
        finally
        {
            Volatile.Write(ref _isScanningMtu, 0);
        }
    }

    //запуск фонового мониторинга трафика и обнаружения мертвого канала
    private void StartTrafficMonitor()
    {
        var token = _cts?.Token ?? CancellationToken.None;
        if (token == CancellationToken.None)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            long lastSent = 0;
            long lastReceived = 0;
            long windowLargeTx = 0;
            long windowLargeRx = 0;
            var windowTicks = 0;
            var mtuBlackholeWindows = 0;
            var deadTicks = 0;

            void OnPing(long _) => deadTicks = 0;
            OnPingUpdated += OnPing;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var currentSent = TotalBytesSent;
                    var currentReceived = TotalBytesReceived;
                    OnTrafficUpdated?.Invoke(currentSent, currentReceived);

                    var armedTicks = Volatile.Read(ref _watchdogArmedTicks);
                    if (armedTicks == 0)
                    {
                        deadTicks = 0;
                        windowTicks = 0;
                        mtuBlackholeWindows = 0;
                        lastSent = currentSent;
                        lastReceived = currentReceived;
                        windowLargeTx = Volatile.Read(ref _largeTxPackets);
                        windowLargeRx = Volatile.Read(ref _largeRxPackets);
                        await Task.Delay(200, token).ConfigureAwait(false);
                        continue;
                    }

                    if (currentSent > lastSent && currentReceived == lastReceived)
                    {
                        deadTicks++;
                        var elapsedMs = Environment.TickCount64 - armedTicks;
                        var isInitialBlackhole = elapsedMs is >= 15000 and < 60000 && currentSent > 25000 && currentReceived == 0;

                        var isDead = (isInitialBlackhole && deadTicks >= 50) ||
                                     (currentSent > 50000 && currentReceived == 0 && deadTicks >= 60) ||
                                     deadTicks >= 90;

                        if (isDead)
                        {
                            Debug.WriteLine($"[ENGINE] Dead connection detected. InitialBlackhole={isInitialBlackhole}, TX={currentSent}, RX={currentReceived}, DeadTicks={deadTicks}");
                            OnDeadConnectionDetected?.Invoke();
                            break;
                        }
                    }
                    else if (currentReceived > lastReceived)
                    {
                        deadTicks = 0;
                    }

                    windowTicks++;
                    if (windowTicks >= 25)
                    {
                        windowTicks = 0;
                        var curLargeTx = Volatile.Read(ref _largeTxPackets);
                        var curLargeRx = Volatile.Read(ref _largeRxPackets);
                        var deltaLargeTx = curLargeTx - windowLargeTx;
                        var deltaLargeRx = curLargeRx - windowLargeRx;
                        windowLargeTx = curLargeTx;
                        windowLargeRx = curLargeRx;

                        if (SmartMtuEngine.CurrentMtu > SmartMtuEngine.MinMtu && Volatile.Read(ref _isScanningMtu) == 0)
                        {
                            if (deltaLargeTx >= 10 && deltaLargeRx == 0)
                            {
                                mtuBlackholeWindows++;
                                if (mtuBlackholeWindows >= 2)
                                {
                                    mtuBlackholeWindows = 0;
                                    var fallbackMtu = SmartMtuEngine.CurrentMtu > 1300 ? 1280 : SmartMtuEngine.MinMtu;
                                    SmartMtuEngine.CurrentMtu = fallbackMtu;
                                    _ = _transport?.SendMtuSyncAsync(fallbackMtu);
                                    OnMtuUpdated?.Invoke(fallbackMtu);
                                    AppLogger.LogWarning($"[SMART MTU] Mid-session MTU blackhole detected (LargeTX={deltaLargeTx}, LargeRX=0). Stepped down to {fallbackMtu}B and rescheduling scan.");
                                    ScheduleSmartMtuScan(4000);
                                }
                            }
                            else if (deltaLargeRx > 0)
                            {
                                mtuBlackholeWindows = 0;
                            }
                        }
                        else
                        {
                            mtuBlackholeWindows = 0;
                        }
                    }

                    lastSent = currentSent;
                    lastReceived = currentReceived;
                    await Task.Delay(200, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                OnPingUpdated -= OnPing;
            }
        }, token);
    }
}
