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
        catch (Exception)
        {
            //подавление ошибки отмены предыдущего токена сканирования
        }

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
            catch (Exception)
            {
                //подавление отмены задачи фонового замера mtu
            }
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
                if (await transport.ProbeMtuAsync(SmartMtuEngine.DefaultMtu, ct).ConfigureAwait(false))
                {
                    optimalMtu = SmartMtuEngine.DefaultMtu;
                }
                else if (await transport.ProbeMtuAsync(SmartMtuEngine.MinMtu, ct).ConfigureAwait(false))
                {
                    optimalMtu = SmartMtuEngine.MinMtu;
                }
                else
                {
                    //даже минимальный зонд 1200 байт завис после прокачки окна — признак блокировки тспу по объему байт
                    if (transport is ObxodkaStreamTransport && !ct.IsCancellationRequested)
                    {
                        Volatile.Write(ref _isScanningMtu, 0);
                        if (await TryEscalateToShieldAsync("PostHandshakeMtuBurstFreeze", ct).ConfigureAwait(false))
                        {
                            return SmartMtuEngine.CurrentMtu;
                        }
                    }

                    optimalMtu = SmartMtuEngine.MinMtu;
                }
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
            long rxStallStartSent = 0;
            long windowLargeTx = 0;
            long windowLargeRx = 0;
            var windowTicks = 0;
            var mtuBlackholeWindows = 0;
            var deadTicks = 0;

            void OnPing(long _)
            {
                deadTicks = 0;
                rxStallStartSent = TotalBytesSent;
            }
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
                        rxStallStartSent = currentSent;
                        windowLargeTx = Volatile.Read(ref _largeTxPackets);
                        windowLargeRx = Volatile.Read(ref _largeRxPackets);
                        await Task.Delay(200, token).ConfigureAwait(false);
                        continue;
                    }

                    if (currentSent > lastSent && currentReceived == lastReceived)
                    {
                        deadTicks++;
                        var elapsedMs = Environment.TickCount64 - armedTicks;
                        var bytesSentDuringStall = currentSent - rxStallStartSent;

                        //периодическая проверка живучести канала эхо-зондом при накоплении задержки
                        if (deadTicks >= 35 && deadTicks % 25 == 0)
                        {
                            _ = _transport?.SendPingProbeAsync();
                        }

                        //раннее обнаружение заморозки прямого h2 соединения тспу на пороге 16-32 кб с бесшовным уходом на щит
                        if (_transport is ObxodkaStreamTransport &&
                            deadTicks >= 25 &&
                            (currentSent >= 16384 || bytesSentDuringStall >= 8192))
                        {
                            var escalated = await TryEscalateToShieldAsync("DirectStreamByteCapFreeze", token).ConfigureAwait(false);
                            if (escalated)
                            {
                                deadTicks = 0;
                                lastSent = TotalBytesSent;
                                lastReceived = TotalBytesReceived;
                                rxStallStartSent = lastSent;
                                continue;
                            }
                        }

                        var isInitialBlackhole = currentReceived < 5000 &&
                                                 elapsedMs is >= 12000 and < 60000 &&
                                                 (currentSent > 20000 || bytesSentDuringStall > 16384) &&
                                                 deadTicks >= 45;

                        var isDead = isInitialBlackhole ||
                                     (_transport is ObxodkaStreamTransport && bytesSentDuringStall > 32768 && deadTicks >= 55) ||
                                     (currentReceived < 5000 && deadTicks >= 70) ||
                                     deadTicks >= 150;

                        if (isDead)
                        {
                            AppLogger.LogWarning($"[ENGINE] Dead connection detected. InitialBlackhole={isInitialBlackhole}, TX={currentSent}, RX={currentReceived}, StallTX={bytesSentDuringStall}, DeadTicks={deadTicks}");
                            OnDeadConnectionDetected?.Invoke();
                            break;
                        }
                    }
                    else if (currentReceived > lastReceived)
                    {
                        deadTicks = 0;
                        rxStallStartSent = currentSent;
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
                            if (deltaLargeTx >= 10 && deltaLargeRx == 0 && currentReceived == lastReceived)
                            {
                                mtuBlackholeWindows++;
                                if (mtuBlackholeWindows >= 3)
                                {
                                    mtuBlackholeWindows = 0;
                                    var fallbackMtu = SmartMtuEngine.CurrentMtu > 1300 ? 1280 : SmartMtuEngine.MinMtu;
                                    SmartMtuEngine.CurrentMtu = fallbackMtu;
                                    _ = _transport?.SendMtuSyncAsync(fallbackMtu);
                                    OnMtuUpdated?.Invoke(fallbackMtu);
                                    AppLogger.LogWarning($"[SMART MTU] Mid-session MTU blackhole detected (LargeTX={deltaLargeTx}, RX=0). Stepped down to {fallbackMtu}B and rescheduling scan.");
                                    ScheduleSmartMtuScan(4000);
                                }
                            }
                            else
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
