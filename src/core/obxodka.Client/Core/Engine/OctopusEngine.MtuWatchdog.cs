namespace obxodka.Core;

public sealed partial class OctopusEngine
{
    private int _isScanningMtu;
    private CancellationTokenSource? _mtuScanCts;
    private long _watchdogArmedTicks;

    //взвод сторожевого таймера контроля зависания трафика
    //включение фонового сторожевого контроля трафика и калибровки mtu
    public void ArmTrafficWatchdog()
    {
        Volatile.Write(ref _watchdogArmedTicks, Environment.TickCount64);
        Volatile.Write(ref _lastHealthyRxTicks, Stopwatch.GetTimestamp());
        if (_transport is ObxodkaWebSocketTransport or ObxodkaXhttpTransport or ObxodkaRealityTransport)
        {
            //на облачных edge-каналах не бьем пачками зондов сразу после хэндшейка чтобы не триггерить тспу/waf
            LockEdgeTransportMtu(_transport);
            return;
        }

        ScheduleSmartMtuScan(4000);
    }

    //фиксация безопасного mtu 1280 для облачных транспортов с синхронизацией ос и сервера
    private void LockEdgeTransportMtu(IVpnTransport? transport)
    {
        if (transport is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            const int targetMtu = SmartMtuEngine.DefaultMtu;
            try
            {
                SmartMtuEngine.CurrentMtu = targetMtu;
                EmitStatus(VpnStatusMessages.MtuSyncingWithServer(targetMtu));
                await transport.SendMtuSyncAsync(targetMtu).ConfigureAwait(false);
                OnMtuUpdated?.Invoke(targetMtu);
                EmitStatus(VpnStatusMessages.MtuLocked(targetMtu));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLogger.LogWarning($"[SMART MTU] Edge MTU lock sync error: {ex.Message}");
            }
        });
    }

    //планирование отложенного интеллектуального замера mtu
    public void ScheduleSmartMtuScan(int delayMs = 2500)
    {
        var engineCts = _cts;
        if (engineCts is null || engineCts.IsCancellationRequested)
        {
            return;
        }

        var scanCts = CancellationTokenSource.CreateLinkedTokenSource(engineCts.Token);
        var prevScanCts = Interlocked.Exchange(ref _mtuScanCts, scanCts);
        if (prevScanCts is not null)
        {
            try
            {
                using (prevScanCts)
                {
                    prevScanCts.Cancel();
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or AggregateException)
            {
                //подавление ошибки отмены предыдущего токена сканирования
            }
        }

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
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or TimeoutException or IOException
                                           or SocketException)
            {
                //подавление отмены задачи фонового замера mtu
            }
        }, token);
    }

    //двунаправленное зондирование (downlink burst + uplink probe) для нахождения и фиксации оптимального mtu
    public async Task<int> ScanAndLockOptimalMtuAsync(CancellationToken ct = default)
    {
        var transport = _transport;
        if (!IsConnected || transport is null || Interlocked.CompareExchange(ref _isScanningMtu, 1, 0) != 0)
        {
            return SmartMtuEngine.CurrentMtu;
        }

        try
        {
            EmitStatus(VpnStatusMessages.ScanningMtuHole(SmartMtuEngine.MaxMtu, SmartMtuEngine.MinMtu));
            var burstResults = await transport.MeasureBurstMtuAsync(ct).ConfigureAwait(false);
            var optimalMtu = SmartMtuEngine.DefaultMtu;

            if (burstResults.Length > 0)
            {
                var downlinkMax = burstResults.Max();

                //проверяем проходимость найденного кандидата и в обратном направлении (uplink probe)
                var steps = SmartMtuEngine.MtuEscalationSteps;
                for (var i = steps.Length - 1; i >= 0; i--)
                {
                    var candidate = steps[i];
                    if (candidate > downlinkMax)
                    {
                        continue;
                    }

                    if (!await transport.ProbeMtuAsync(candidate, ct).ConfigureAwait(false))
                    {
                        continue;
                    }

                    optimalMtu = candidate;
                    break;
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
                    if (transport is ObxodkaStreamTransport &&
                        !ct.IsCancellationRequested &&
                        await TryEscalateToShieldAsync("PostHandshakeMtuBurstFreeze", ct).ConfigureAwait(false))
                    {
                        return SmartMtuEngine.CurrentMtu;
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

                        //ранний эхо-зонд при застое rx для быстрой проверки канала (на облачных каналах зондируем на 3с и 5с)
                        var isDirectStream = _transport is ObxodkaStreamTransport;
                        if (ShouldSendWatchdogPing(isDirectStream, deadTicks))
                        {
                            _ = _transport?.SendPingProbeAsync();
                        }

                        //раннее переключение по круговому кольцу: для прямого потока при тспу 5с, для облачных щитов 9с с подтвержденным провалом пинга
                        if (ShouldRotateForRxStall(isDirectStream, deadTicks, currentSent, bytesSentDuringStall))
                        {
                            var reason = isDirectStream
                                ? "DirectStreamByteCapFreeze"
                                : "MidSessionRxStall";
                            var rotated = await TryRotateProtocolAsync(reason, token).ConfigureAwait(false);
                            if (rotated)
                            {
                                deadTicks = 0;
                                lastSent = TotalBytesSent;
                                lastReceived = TotalBytesReceived;
                                rxStallStartSent = lastSent;
                                continue;
                            }
                        }

                        var isInitialBlackhole = currentReceived < 5000 &&
                                                 elapsedMs is >= 10000 and < 60000 &&
                                                 (currentSent > 12000 || bytesSentDuringStall > 8192) &&
                                                 deadTicks >= (isDirectStream ? 35 : 55);

                        var isDead = isInitialBlackhole ||
                                     bytesSentDuringStall > 32768 && deadTicks >= (isDirectStream ? 45 : 65) ||
                                     currentReceived < 5000 && deadTicks >= (isDirectStream ? 55 : 70) ||
                                     deadTicks >= 90;

                        if (isDead)
                        {
                            if (await TryRotateProtocolAsync("DeadConnectionRecovery", token).ConfigureAwait(false))
                            {
                                deadTicks = 0;
                                lastSent = TotalBytesSent;
                                lastReceived = TotalBytesReceived;
                                rxStallStartSent = lastSent;
                                continue;
                            }

                            AppLogger.LogWarning(
                                $"[ENGINE] Dead connection detected across entire ring. InitialBlackhole={isInitialBlackhole}, TX={currentSent}, RX={currentReceived}, StallTX={bytesSentDuringStall}, DeadTicks={deadTicks}");
                            OnDeadConnectionDetected?.Invoke();
                            break;
                        }
                    }
                    else if (currentReceived > lastReceived)
                    {
                        deadTicks = 0;
                        rxStallStartSent = currentSent;
                    }
                    else
                    {
                        //контроль тихой заморозки без активного tx (протухший keepalive/edge у cloudflare или тспу)
                        var lastHealthy = Volatile.Read(ref _lastHealthyRxTicks);
                        if (lastHealthy > 0)
                        {
                            var silenceSec = (Stopwatch.GetTimestamp() - lastHealthy) / (double)Stopwatch.Frequency;
                            switch (silenceSec)
                            {
                                case >= 18.0 when Environment.TickCount64 - armedTicks >= 5000 &&
                                                  await TryRotateProtocolAsync("PongSilenceTimeout", token).ConfigureAwait(false):
                                    deadTicks = 0;
                                    lastSent = TotalBytesSent;
                                    lastReceived = TotalBytesReceived;
                                    rxStallStartSent = lastSent;
                                    continue;
                                case >= 14.0 when deadTicks % 10 == 0:
                                    _ = _transport?.SendPingProbeAsync();
                                    break;
                            }
                        }
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
                                    AppLogger.LogWarning(
                                        $"[SMART MTU] Mid-session MTU blackhole detected (LargeTX={deltaLargeTx}, RX=0). Stepped down to {fallbackMtu}B and rescheduling scan.");
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
            catch (OperationCanceledException)
            {
                //ожидаемая остановка фонового сторожа mtu
            }
            catch (ObjectDisposedException)
            {
                //источник отмены освобожден при остановке движка
            }
            finally
            {
                OnPingUpdated -= OnPing;
            }

            return;

            void OnPing(long _)
            {
                deadTicks = 0;
                rxStallStartSent = TotalBytesSent;
            }
        }, token);
    }

    //проверка момента отправки активного эхо-зонда при застое входящего трафика
    private static bool ShouldSendWatchdogPing(bool isDirectStream, int deadTicks) =>
        isDirectStream
            ? deadTicks == 20 || deadTicks >= 25 && deadTicks % 15 == 0
            : deadTicks is 15 or 25 || deadTicks >= 35 && deadTicks % 15 == 0;

    //проверка необходимости ранней ротации протокола при зависании входящего трафика
    private static bool ShouldRotateForRxStall(bool isDirectStream, int deadTicks, long currentSent, long bytesSentDuringStall) =>
        isDirectStream
            ? deadTicks >= 25 && (currentSent >= 16384 || bytesSentDuringStall >= 8192)
            : deadTicks >= 55 || deadTicks >= 45 && bytesSentDuringStall >= 4096;
}
