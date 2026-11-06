namespace obxodka.Core.Transports;

public sealed partial class ObxodkaStreamTransport
{
    //фоновый цикл проверки состояния каналов, пинга и обнаружения блэкхолов
    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var nowTs = Stopwatch.GetTimestamp();
            var rx0 = Volatile.Read(ref _lastRxTicks0);
            var rx1 = Volatile.Read(ref _lastRxTicks1);
            var lastActivityTicks = Math.Max(Volatile.Read(ref _lastTxTicks), Math.Max(rx0, rx1));
            var idleSec = lastActivityTicks > 0 ? (nowTs - lastActivityTicks) / (double)Stopwatch.Frequency : 0;
            var delayMs = idleSec < 3.0
                ? RandomNumberGenerator.GetInt32(1100, 2300) + RandomNumberGenerator.GetInt32(0, 850)
                : RandomNumberGenerator.GetInt32(2400, 4800) + RandomNumberGenerator.GetInt32(0, 1400);

            try
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            nowTs = Stopwatch.GetTimestamp();
            rx0 = Volatile.Read(ref _lastRxTicks0);
            rx1 = Volatile.Read(ref _lastRxTicks1);
            var connTicks = Volatile.Read(ref _connectedAtTicks);
            var activeRays = Volatile.Read(ref _activeRays);

            //проверка асимметричного блэкхола, когда отправка идет, а прием завис
            if (_inspector.DetectAsymmetricBlackhole(Volatile.Read(ref _preferredRay), nowTs, out var fallbackRay))
            {
                var prevRay = Volatile.Read(ref _preferredRay);
                Volatile.Write(ref _preferredRay, fallbackRay);
                Volatile.Write(ref _hasClearPreferredRay, 1);
                AppLogger.LogWarning($"[RAY-INSPECTOR] Asymmetric blackhole detected on Ray #{prevRay} (TX active, RX stalled). Instantly migrated primary traffic to Ray #{fallbackRay}.");
            }

            //проверка застревания пакетов по mtu
            if (_inspector.DetectMtuBlackhole(nowTs))
            {
                var currentMtu = SmartMtuEngine.CurrentMtu;
                if (_inspector.TryDownshiftMtu(currentMtu, out var newMtu))
                {
                    SmartMtuEngine.CurrentMtu = newMtu;
                    _ = SendMtuSyncAsync(newMtu);
                    OnMtuUpdated?.Invoke(newMtu);
                    AppLogger.LogWarning($"[RAY-INSPECTOR] Adaptive MTU downshifted by inspector: {currentMtu} -> {newMtu}");
                }
                else
                {
                    _inspector.RecordStep($"MTU blackhole persisted at minimum MTU {currentMtu}");
                    _ = _inspector.ReportIncidentIfExhaustedAsync(
                        reason: "MtuBlackholeAtMinMtu",
                        targetHost: ActiveSni,
                        effectivePort: EffectivePort,
                        currentMtu: currentMtu,
                        targetIp: _activeServerIp,
                        apiBaseUrl: AppConfig.ApiBaseUrl,
                        cancellationToken: CancellationToken.None);
                }
            }

            //проверка лучей которые не завершили рукопожатие за отведенное время
            if (connTicks > 0 && ((nowTs - connTicks) * 1000.0 / Stopwatch.Frequency) > 8000)
            {
                if (rx0 == 0 && rx1 > 0 && _rayCts[0] != null)
                {
                    AppLogger.LogWarning("[RAY-HEALTH] Ray #0 never completed handshake. Aborting to hop port.");
                    Volatile.Write(ref _lastRxTicks0, nowTs);
                    try
                    {
                        if (_rayCts[0] is { } cts0)
                        {
                            await cts0.CancelAsync().ConfigureAwait(false);
                        }
                    }
                    catch { }
                }
                else if (rx1 == 0 && rx0 > 0 && _rayCts[1] != null)
                {
                    AppLogger.LogWarning("[RAY-HEALTH] Ray #1 never completed handshake. Aborting to hop port.");
                    Volatile.Write(ref _lastRxTicks1, nowTs);
                    try
                    {
                        if (_rayCts[1] is { } cts1)
                        {
                            await cts1.CancelAsync().ConfigureAwait(false);
                        }
                    }
                    catch { }
                }
            }

            //переключение трафика при зависании одного из активных лучей
            if (rx0 > 0 && rx1 > 0 && activeRays > 1)
            {
                var silence0Ms = (nowTs - rx0) * 1000.0 / Stopwatch.Frequency;
                var silence1Ms = (nowTs - rx1) * 1000.0 / Stopwatch.Frequency;

                if (silence0Ms > 8000 && silence1Ms < 3000)
                {
                    AppLogger.LogWarning($"[RAY-HEALTH] Ray #0 stalled (silence {silence0Ms:F0}ms). Aborting Ray #0 socket & shifting 100% traffic to Ray #1.");
                    Volatile.Write(ref _activeRays, 1);
                    Volatile.Write(ref _preferredRay, 1);
                    Volatile.Write(ref _hasClearPreferredRay, 1);
                    try
                    {
                        if (_rayCts[0] is { } cts0)
                        {
                            await cts0.CancelAsync().ConfigureAwait(false);
                        }
                    }
                    catch { }
                    while (_txChannels[0].TryDequeue(out var leftover))
                    {
                        if (!_txChannels[1].TryEnqueue(leftover.buffer, leftover.length))
                        {
                            ArrayPool<byte>.Shared.Return(leftover.buffer);
                        }
                    }
                }
                else if (silence1Ms > 8000 && silence0Ms < 3000)
                {
                    AppLogger.LogWarning($"[RAY-HEALTH] Ray #1 stalled (silence {silence1Ms:F0}ms). Aborting Ray #1 socket & shifting 100% traffic to Ray #0.");
                    Volatile.Write(ref _activeRays, 1);
                    Volatile.Write(ref _preferredRay, 0);
                    Volatile.Write(ref _hasClearPreferredRay, 1);
                    try
                    {
                        if (_rayCts[1] is { } cts1)
                        {
                            await cts1.CancelAsync().ConfigureAwait(false);
                        }
                    }
                    catch { }
                    while (_txChannels[1].TryDequeue(out var leftover))
                    {
                        if (!_txChannels[0].TryEnqueue(leftover.buffer, leftover.length))
                        {
                            ArrayPool<byte>.Shared.Return(leftover.buffer);
                        }
                    }
                }
                else if (silence0Ms < 3000 && silence1Ms < 3000 && activeRays < 2)
                {
                    Volatile.Write(ref _activeRays, 2);
                }
            }
            else if (activeRays <= 1)
            {
                var pref = Volatile.Read(ref _preferredRay);
                var rxPref = pref == 0 ? rx0 : rx1;
                if (rxPref > 0)
                {
                    var silenceMs = (nowTs - rxPref) * 1000.0 / Stopwatch.Frequency;
                    if (silenceMs > 25000)
                    {
                        AppLogger.LogWarning($"[RAY-HEALTH] Active Ray #{pref} stalled (silence {silenceMs:F0}ms). Aborting socket to trigger port failover.");
                        Volatile.Write(ref pref == 0 ? ref _lastRxTicks0 : ref _lastRxTicks1, nowTs);
                        try
                        {
                            if (_rayCts[pref] is { } ctsPref)
                            {
                                await ctsPref.CancelAsync().ConfigureAwait(false);
                            }
                        }
                        catch { }
                    }
                }
            }

            //формирование маски живых лучей
            byte rxAliveMask = 0;
            if (rx0 > 0 && ((nowTs - rx0) * 1000.0 / Stopwatch.Frequency) < 10000)
            {
                rxAliveMask |= 0x01;
            }
            if (rx1 > 0 && ((nowTs - rx1) * 1000.0 / Stopwatch.Frequency) < 10000)
            {
                rxAliveMask |= 0x02;
            }
            if (_inspector.IsRayAlive(2, nowTs, 10000))
            {
                rxAliveMask |= 0x04;
            }
            if (rxAliveMask == 0)
            {
                rxAliveMask = (byte)(1 << Volatile.Read(ref _preferredRay));
            }
            if (Volatile.Read(ref _hasClearPreferredRay) == 1)
            {
                rxAliveMask |= Volatile.Read(ref _preferredRay) == 0 ? (byte)0x10 : (byte)0x20;
            }

            //отправка зондов пинга во все активные каналы
            for (var r = 0; r < Math.Min(activeRays, 3); r++)
            {
                if (_rayCts[r] == null)
                {
                    continue;
                }

                var rxRay = _inspector.GetLastRxTicks(r);
                var rttRay = _inspector.GetRayRtt(r);
                if (rxRay > 0 && rttRay > 0 && ((nowTs - rxRay) * 1000.0 / Stopwatch.Frequency) < 400 && RandomNumberGenerator.GetInt32(2) == 0)
                {
                    continue;
                }

                var jitterPadding = RandomNumberGenerator.GetInt32(0, 16);
                var pingLen = 12 + jitterPadding;
                var pingBuf = ArrayPool<byte>.Shared.Rent(pingLen);
                pingBuf[0] = 0xAA;
                pingBuf[1] = 0xBB;
                BinaryPrimitives.WriteInt64LittleEndian(pingBuf.AsSpan(2, 8), nowTs);
                pingBuf[10] = rxAliveMask;
                pingBuf[11] = (byte)r;
                if (jitterPadding > 0)
                {
                    RandomNumberGenerator.Fill(pingBuf.AsSpan(12, jitterPadding));
                }

                _inspector.RecordTx(r, nowTs);
                if (!_txChannels[r].TryEnqueue(pingBuf, pingLen))
                {
                    ArrayPool<byte>.Shared.Return(pingBuf);
                }
            }
        }
    }

    //отправка исходящего ip-пакета из пула буферов
    public void SendPacketFromPool(byte[] packet, int length)
    {
        if (!Volatile.Read(ref _connected))
        {
            ArrayPool<byte>.Shared.Return(packet);
            return;
        }

        _inspector.RecordDataTx();
        var nowTs = Stopwatch.GetTimestamp();
        Volatile.Write(ref _lastTxTicks, nowTs);

        var aliveMask = _inspector.GetAliveMask(nowTs);
        var preferredRay = Volatile.Read(ref _preferredRay);

        //маршрутизация пользовательского пакета через рабочий луч
        PacketRouter.GetRays(
            packet.AsSpan(0, length),
            aliveMask,
            preferredRay,
            out var ray0,
            out var duplicateRay);

        _inspector.RecordTx(ray0, nowTs);

        if (!_txChannels[ray0].TryEnqueue(packet, length))
        {
            ArrayPool<byte>.Shared.Return(packet);
            return;
        }

        //хеджирование дублированием во второй рабочий луч ray 0 или ray 1
        if (duplicateRay is 0 or 1 && duplicateRay != ray0)
        {
            var dup = ArrayPool<byte>.Shared.Rent(length);
            Buffer.BlockCopy(packet, 0, dup, 0, length);
            _inspector.RecordTx(duplicateRay, nowTs);
            if (!_txChannels[duplicateRay].TryEnqueue(dup, length))
            {
                ArrayPool<byte>.Shared.Return(dup);
            }
        }
    }

    //отправка одиночного контрольного зонда пинга
    public Task SendPingProbeAsync()
    {
        var nowTs = Stopwatch.GetTimestamp();
        var rx0 = Volatile.Read(ref _lastRxTicks0);
        var rx1 = Volatile.Read(ref _lastRxTicks1);
        var rx2 = Volatile.Read(ref _lastRxTicks2);
        byte rxAliveMask = 0;
        if (rx0 > 0 && ((nowTs - rx0) * 1000.0 / Stopwatch.Frequency) < 3500)
        {
            rxAliveMask |= 0x01;
        }
        if (rx1 > 0 && ((nowTs - rx1) * 1000.0 / Stopwatch.Frequency) < 3500)
        {
            rxAliveMask |= 0x02;
        }
        if (rx2 > 0 && ((nowTs - rx2) * 1000.0 / Stopwatch.Frequency) < 3500)
        {
            rxAliveMask |= 0x04;
        }
        if ((rxAliveMask & 0x07) == 0)
        {
            rxAliveMask = (byte)(1 << Math.Clamp(Volatile.Read(ref _preferredRay), 0, 2));
        }

        var pref = Volatile.Read(ref _preferredRay);
        if (pref == 0)
        {
            rxAliveMask |= 0x10;
        }
        else if (pref == 1)
        {
            rxAliveMask |= 0x20;
        }

        var activeRaysCount = Volatile.Read(ref _activeRays);
        var raysToSend = Math.Min(_txChannels.Length, Math.Max(1, activeRaysCount));
        for (var r = 0; r < raysToSend; r++)
        {
            var pingBuf = ArrayPool<byte>.Shared.Rent(11);
            pingBuf[0] = 0xAA;
            pingBuf[1] = 0xBB;
            BinaryPrimitives.WriteInt64LittleEndian(pingBuf.AsSpan(2, 8), nowTs);
            pingBuf[10] = rxAliveMask;
            if (!_txChannels[r].TryEnqueue(pingBuf, 11))
            {
                ArrayPool<byte>.Shared.Return(pingBuf);
            }
        }

        return Task.CompletedTask;
    }

    //проверка проходимости исходящего mtu заданного размера через активный луч
    public async Task<bool> ProbeMtuAsync(int size, CancellationToken ct)
    {
        if (!IsConnected || _cts?.IsCancellationRequested == true)
        {
            return false;
        }

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nowTs = Stopwatch.GetTimestamp();
        _pendingMtuProbes[nowTs] = tcs;

        var buf = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            buf[0] = 0xAA;
            buf[1] = 0xBB;
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(2, 8), nowTs);

            var rx0 = Volatile.Read(ref _lastRxTicks0);
            var rx1 = Volatile.Read(ref _lastRxTicks1);
            byte rxAliveMask = 0;
            if (rx0 > 0 && ((nowTs - rx0) * 1000.0 / Stopwatch.Frequency) < 3500)
            {
                rxAliveMask |= 0x01;
            }
            if (rx1 > 0 && ((nowTs - rx1) * 1000.0 / Stopwatch.Frequency) < 3500)
            {
                rxAliveMask |= 0x02;
            }
            if (rxAliveMask == 0)
            {
                rxAliveMask = (byte)(1 << Volatile.Read(ref _preferredRay));
            }
            buf[10] = rxAliveMask;
            RandomNumberGenerator.Fill(buf.AsSpan(11, size - 11));

            var targetRay = Volatile.Read(ref _activeRays) > 1 ? 0 : Volatile.Read(ref _preferredRay);
            if (!_txChannels[targetRay].TryEnqueue(buf, size))
            {
                ArrayPool<byte>.Shared.Return(buf);
                return false;
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(TimeSpan.FromMilliseconds(1200));
            using var reg = linkedCts.Token.Register(() => tcs.TrySetResult(0));

            var returnedLen = await tcs.Task.ConfigureAwait(false);
            return returnedLen >= size - 2;
        }
        finally
        {
            _ = _pendingMtuProbes.TryRemove(nowTs, out _);
        }
    }

    //сигнал корректного завершения соединения с отправкой кадра CmdDisconnect серверу
    public async Task SendDisconnectSignalAsync()
    {
        if (IsConnected && _cts is { IsCancellationRequested: false })
        {
            var discBuf = ArrayPool<byte>.Shared.Rent(2);
            discBuf[0] = 0xEE;
            discBuf[1] = 0x99;
            var targetRay = Math.Clamp(Volatile.Read(ref _preferredRay), 0, _txChannels.Length - 1);
            if (_txChannels[targetRay].TryEnqueue(discBuf, 2))
            {
                try
                {
                    await Task.Delay(25).ConfigureAwait(false);
                }
                catch { }
            }
            else
            {
                ArrayPool<byte>.Shared.Return(discBuf);
            }
        }

        if (_cts != null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
    }

    //серия тестовых пакетов для замера допустимого входящего mtu
    public async Task<int[]> MeasureBurstMtuAsync(CancellationToken ct = default)
    {
        if (!IsConnected || _cts?.IsCancellationRequested == true)
        {
            return [];
        }

        lock (_receivedBurstMtus)
        {
            _receivedBurstMtus.Clear();
        }

        var buf = ArrayPool<byte>.Shared.Rent(2);
        buf[0] = 0xEE;
        buf[1] = 0x77;

        var targetRay = Volatile.Read(ref _activeRays) > 1 ? 0 : Volatile.Read(ref _preferredRay);
        if (!_txChannels[targetRay].TryEnqueue(buf, 2))
        {
            ArrayPool<byte>.Shared.Return(buf);
            return [];
        }

        try
        {
            await Task.Delay(1200, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        lock (_receivedBurstMtus)
        {
            return [.. _receivedBurstMtus];
        }
    }

    //синхронизация значения mtu с сервером
    public Task SendMtuSyncAsync(int mtu)
    {
        var buf = ArrayPool<byte>.Shared.Rent(4);
        buf[0] = 0xCC;
        buf[1] = 0xDD;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), (ushort)mtu);
        var targetRay = Volatile.Read(ref _activeRays) > 1 ? 0 : Volatile.Read(ref _preferredRay);
        if (!_txChannels[targetRay].TryEnqueue(buf, 4))
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        return Task.CompletedTask;
    }

    //освобождение синхронных ресурсов
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Volatile.Write(ref _connected, false);
        _cts?.Cancel();
        for (var i = 0; i < _txChannels.Length; i++)
        {
            _txChannels[i].DrainAndReturn(static buf => ArrayPool<byte>.Shared.Return(buf));
            _txChannels[i].Dispose();
        }
        _httpClient0?.Dispose();
        _handler0?.Dispose();
        _httpClient1?.Dispose();
        _handler1?.Dispose();
        _httpClient2?.Dispose();
        _handler2?.Dispose();
        _inspector.Reset();
        _cts?.Dispose();
    }

    //асинхронное освобождение ресурсов
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Volatile.Write(ref _connected, false);
        if (_cts != null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        for (var i = 0; i < _txChannels.Length; i++)
        {
            _txChannels[i].DrainAndReturn(static buf => ArrayPool<byte>.Shared.Return(buf));
            _txChannels[i].Dispose();
        }
        _httpClient0?.Dispose();
        _handler0?.Dispose();
        _httpClient1?.Dispose();
        _handler1?.Dispose();
        _httpClient2?.Dispose();
        _handler2?.Dispose();
        _inspector.Reset();
        _cts?.Dispose();
    }
}
