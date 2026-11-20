namespace obxodka.Core.Transports;

public sealed partial class ObxodkaStreamTransport
{
    //отправка исходящего ip-пакета из пула буферов
    public void SendPacketFromPool(byte[] packet, int length)
    {
        if (!Volatile.Read(ref _connected))
        {
            ArrayPool<byte>.Shared.Return(packet);
            return;
        }

        _inspector.RecordDataTx(packet.AsSpan(0, length));
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

        //хеджирование дублированием во второй рабочий луч до передачи основного буфера в очередь, чтобы избежать гонки возврата в пул
        byte[]? dup = null;
        if (duplicateRay is 0 or 1 && duplicateRay != ray0)
        {
            dup = ArrayPool<byte>.Shared.Rent(length);
            Buffer.BlockCopy(packet, 0, dup, 0, length);
        }

        if (!_txChannels[ray0].TryEnqueue(packet, length))
        {
            ArrayPool<byte>.Shared.Return(packet);
            if (dup is not null)
            {
                ArrayPool<byte>.Shared.Return(dup);
            }

            return;
        }

        if (dup is null)
        {
            return;
        }

        _inspector.RecordTx(duplicateRay, nowTs);
        if (!_txChannels[duplicateRay].TryEnqueue(dup, length))
        {
            ArrayPool<byte>.Shared.Return(dup);
        }
    }

    //отправка одиночного контрольного зонда пинга
    public Task SendPingProbeAsync()
    {
        var nowTs = Stopwatch.GetTimestamp();
        var rxAliveMask = BuildRxAliveMask(nowTs, 3500, true);

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
        if (!IsConnected || _cts?.IsCancellationRequested == true || size < 16)
        {
            return false;
        }

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeTs = Stopwatch.GetTimestamp();
        while (!_pendingMtuProbes.TryAdd(probeTs, tcs))
        {
            probeTs++;
        }

        var buf = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            buf[0] = 0xAA;
            buf[1] = 0xBB;
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(2, 8), probeTs);

            buf[10] = BuildRxAliveMask(Stopwatch.GetTimestamp(), 3500, true);
            RandomNumberGenerator.Fill(buf.AsSpan(11, size - 11));

            var targetRay = SelectHealthyProbeRay();
            if (!_txChannels[targetRay].TryEnqueue(buf, size))
            {
                ArrayPool<byte>.Shared.Return(buf);
                return false;
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(TimeSpan.FromMilliseconds(1200));
            await using var reg = linkedCts.Token.Register(() => tcs.TrySetResult(0));

            var returnedLen = await tcs.Task.ConfigureAwait(false);
            if (returnedLen == 0 && !ct.IsCancellationRequested)
            {
                _inspector.RecordProbeTimeout(targetRay);
            }

            return returnedLen >= size - 2;
        }
        finally
        {
            _ = ((IDictionary<long, TaskCompletionSource<int>>)_pendingMtuProbes).Remove(probeTs);
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
                catch (OperationCanceledException)
                {
                    //подавление ошибки паузы отправки сигнала отключения
                }
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

        var targetRay = Volatile.Read(ref _activeRays) > 1 ? 0 : Math.Clamp(Volatile.Read(ref _preferredRay), 0, 1);
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
            //штатная отмена замера пакетного mtu
        }

        lock (_receivedBurstMtus)
        {
            return [.. _receivedBurstMtus];
        }
    }

    //синхронизация значения mtu с сервером и ожидание подтверждения
    public async Task SendMtuSyncAsync(int mtu)
    {
        if (!IsConnected || _cts is { IsCancellationRequested: true })
        {
            return;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (_pendingMtuSyncAcks.TryRemove(mtu, out var staleTcs))
            {
                _ = staleTcs.TrySetResult(false);
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingMtuSyncAcks[mtu] = tcs;

            var buf = ArrayPool<byte>.Shared.Rent(4);
            buf[0] = 0xCC;
            buf[1] = 0xDD;
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), (ushort)mtu);
            var targetRay = SelectHealthyProbeRay();
            if (!_txChannels[targetRay].TryEnqueue(buf, 4))
            {
                ArrayPool<byte>.Shared.Return(buf);
                _ = _pendingMtuSyncAcks.TryRemove(mtu, out _);
                return;
            }

            try
            {
                var sessionToken = _cts?.Token ?? CancellationToken.None;
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
                timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(1500));
                await using var reg = timeoutCts.Token.Register(() => tcs.TrySetResult(false));
                if (await tcs.Task.ConfigureAwait(false))
                {
                    return;
                }
            }
            finally
            {
                if (_pendingMtuSyncAcks.TryGetValue(mtu, out var registered) && ReferenceEquals(registered, tcs))
                {
                    _ = _pendingMtuSyncAcks.TryRemove(mtu, out _);
                }
            }

            if (_cts is { IsCancellationRequested: true } || !IsConnected)
            {
                return;
            }
        }
    }

    //освобождение синхронных ресурсов
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _connected, false);
        CancelPendingControlWaiters();
        _cts?.Cancel();
        foreach (var t in _txChannels)
        {
            t.Dispose();
            t.DrainAndReturn(static buf => ArrayPool<byte>.Shared.Return(buf));
        }

        _httpClient0?.Dispose();
        _httpClient1?.Dispose();
        _httpClient2?.Dispose();
        _inspector.Reset();
        _cts?.Dispose();
    }

    //асинхронное освобождение ресурсов
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _connected, false);
        CancelPendingControlWaiters();
        using var cts = Interlocked.Exchange(ref _cts, null);
        if (cts != null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        var rayTasks = _activeRayTasks;
        _activeRayTasks = [];
        if (rayTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(rayTasks).WaitAsync(TimeSpan.FromSeconds(1.5), CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                //ожидаемая отмена задач лучей при освобождении транспорта
            }
            catch (TimeoutException)
            {
                //таймаут ожидания остановки задач лучей при освобождении транспорта
            }
        }

        foreach (var t in _txChannels)
        {
            t.Dispose();
            t.DrainAndReturn(static buf => ArrayPool<byte>.Shared.Return(buf));
        }

        _httpClient0?.Dispose();
        _httpClient1?.Dispose();
        _httpClient2?.Dispose();
        _inspector.Reset();
    }

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
                ? RandomNumberGenerator.GetInt32(950, 1450)
                : RandomNumberGenerator.GetInt32(1600, 2400);

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
                AppLogger.LogWarning(
                    $"[RAY-INSPECTOR] Asymmetric blackhole detected on Ray #{prevRay} (TX active, RX stalled). Instantly migrated primary traffic to Ray #{fallbackRay}.");
            }

            //проверка застревания пакетов по mtu
            if (_inspector.DetectMtuBlackhole(nowTs))
            {
                var currentMtu = SmartMtuEngine.CurrentMtu;
                if (_inspector.TryDownshiftMtu(currentMtu, out var newMtu))
                {
                    _ = SendMtuSyncAsync(newMtu);
                    OnMtuUpdated?.Invoke(newMtu);
                    AppLogger.LogWarning($"[RAY-INSPECTOR] Adaptive MTU downshifted by inspector: {currentMtu} -> {newMtu}");
                }
                else
                {
                    _inspector.TryRecordMinMtuPersistence(currentMtu, nowTs);
                    _ = _inspector.ReportIncidentIfExhaustedAsync(
                        "MtuBlackholeAtMinMtu",
                        ActiveSni,
                        EffectivePort,
                        currentMtu,
                        _activeServerIp,
                        AppConfig.ApiBaseUrl,
                        CancellationToken.None);
                }
            }

            //проверка лучей которые не завершили рукопожатие за отведенное время
            if (connTicks > 0 && (nowTs - connTicks) * 1000.0 / Stopwatch.Frequency > 8000)
            {
                if (rx0 == 0 && rx1 > 0 && Volatile.Read(ref _rayCts[0]) is { } cts0)
                {
                    AppLogger.LogWarning("[RAY-HEALTH] Ray #0 never completed handshake. Aborting to hop port.");
                    Volatile.Write(ref _lastRxTicks0, nowTs);
                    try
                    {
                        await cts0.CancelAsync().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        //подавление ошибки отмены луча 0
                    }
                }
                else if (rx1 == 0 && rx0 > 0 && Volatile.Read(ref _rayCts[1]) is { } cts1)
                {
                    AppLogger.LogWarning("[RAY-HEALTH] Ray #1 never completed handshake. Aborting to hop port.");
                    Volatile.Write(ref _lastRxTicks1, nowTs);
                    try
                    {
                        await cts1.CancelAsync().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        //подавление ошибки отмены луча 1
                    }
                }
            }

            //переключение и восстановление активности рабочих лучей 0 и 1
            if (rx0 > 0 && rx1 > 0)
            {
                var silence0Ms = (nowTs - rx0) * 1000.0 / Stopwatch.Frequency;
                var silence1Ms = (nowTs - rx1) * 1000.0 / Stopwatch.Frequency;

                if (activeRays > 1)
                {
                    if (silence0Ms > 8000 && silence1Ms < 3000)
                    {
                        _inspector.RecordProbeTimeout(0);
                        AppLogger.LogWarning(
                            $"[RAY-HEALTH] Ray #0 stalled (silence {silence0Ms:F0}ms). Aborting Ray #0 socket & shifting 100% traffic to Ray #1.");
                        Volatile.Write(ref _activeRays, 1);
                        Volatile.Write(ref _preferredRay, 1);
                        Volatile.Write(ref _hasClearPreferredRay, 1);
                        try
                        {
                            if (Volatile.Read(ref _rayCts[0]) is { } cts0)
                            {
                                await cts0.CancelAsync().ConfigureAwait(false);
                            }
                        }
                        catch (ObjectDisposedException)
                        {
                            //подавление ошибки сброса зависшего луча 0
                        }

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
                        _inspector.RecordProbeTimeout(1);
                        AppLogger.LogWarning(
                            $"[RAY-HEALTH] Ray #1 stalled (silence {silence1Ms:F0}ms). Aborting Ray #1 socket & shifting 100% traffic to Ray #0.");
                        Volatile.Write(ref _activeRays, 1);
                        Volatile.Write(ref _preferredRay, 0);
                        Volatile.Write(ref _hasClearPreferredRay, 1);
                        try
                        {
                            if (Volatile.Read(ref _rayCts[1]) is { } cts1)
                            {
                                await cts1.CancelAsync().ConfigureAwait(false);
                            }
                        }
                        catch (ObjectDisposedException)
                        {
                            //подавление ошибки сброса зависшего луча 1
                        }

                        while (_txChannels[1].TryDequeue(out var leftover))
                        {
                            if (!_txChannels[0].TryEnqueue(leftover.buffer, leftover.length))
                            {
                                ArrayPool<byte>.Shared.Return(leftover.buffer);
                            }
                        }
                    }
                }
                else if (silence0Ms < 3000 && silence1Ms < 3000)
                {
                    Volatile.Write(ref _activeRays, 2);
                    Volatile.Write(ref _hasClearPreferredRay, 0);
                    activeRays = 2;
                    AppLogger.Log("[RAY-HEALTH] Both Ray #0 and Ray #1 are healthy again. Restored activeRays = 2.");
                }
            }

            if (activeRays <= 1)
            {
                var pref = Volatile.Read(ref _preferredRay) == 1 ? 1 : 0;
                var rxPref = pref == 0 ? rx0 : rx1;
                if (rxPref > 0)
                {
                    var silenceMs = (nowTs - rxPref) * 1000.0 / Stopwatch.Frequency;
                    if (silenceMs > 25000)
                    {
                        AppLogger.LogWarning(
                            $"[RAY-HEALTH] Active Ray #{pref} stalled (silence {silenceMs:F0}ms). Aborting socket to trigger port failover.");
                        Volatile.Write(ref pref == 0 ? ref _lastRxTicks0 : ref _lastRxTicks1, nowTs);
                        try
                        {
                            if (Volatile.Read(ref _rayCts[pref]) is { } ctsPref)
                            {
                                await ctsPref.CancelAsync().ConfigureAwait(false);
                            }
                        }
                        catch (ObjectDisposedException)
                        {
                            //подавление ошибки отмены предпочтительного луча
                        }
                    }
                }
            }

            //формирование маски живых лучей
            var rxAliveMask = BuildRxAliveMask(nowTs, 10000, Volatile.Read(ref _hasClearPreferredRay) == 1);

            //отправка зондов пинга во все активные каналы
            for (var r = 0; r < Math.Min(activeRays, 3); r++)
            {
                if (Volatile.Read(ref _rayCts[r]) == null)
                {
                    continue;
                }

                var rxRay = _inspector.GetLastRxTicks(r);
                var rttRay = _inspector.GetRayRtt(r);
                if (rxRay > 0 && rttRay > 0 && (nowTs - rxRay) * 1000.0 / Stopwatch.Frequency < 250)
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

                if (!_txChannels[r].TryEnqueue(pingBuf, pingLen))
                {
                    ArrayPool<byte>.Shared.Return(pingBuf);
                }
            }
        }
    }

    //сборка битовой маски живых лучей и флагов предпочтительного канала
    private byte BuildRxAliveMask(long nowTs, double maxSilenceMs, bool includePreferredFlags)
    {
        var rx0 = Volatile.Read(ref _lastRxTicks0);
        var rx1 = Volatile.Read(ref _lastRxTicks1);
        var rx2 = Volatile.Read(ref _lastRxTicks2);
        byte mask = 0;

        if (rx0 > 0 && (nowTs - rx0) * 1000.0 / Stopwatch.Frequency < maxSilenceMs)
        {
            mask |= 0x01;
        }

        if (rx1 > 0 && (nowTs - rx1) * 1000.0 / Stopwatch.Frequency < maxSilenceMs)
        {
            mask |= 0x02;
        }

        if (rx2 > 0 && (nowTs - rx2) * 1000.0 / Stopwatch.Frequency < maxSilenceMs || _inspector.IsRayAlive(2, nowTs, maxSilenceMs))
        {
            mask |= 0x04;
        }

        var pref = Math.Clamp(Volatile.Read(ref _preferredRay), 0, 2);
        if ((mask & 0x07) == 0)
        {
            mask = (byte)(1 << pref);
        }

        if (!includePreferredFlags)
        {
            return mask;
        }

        switch (pref)
        {
            case 0:
                mask |= 0x10;
                break;
            case 1:
                mask |= 0x20;
                break;
        }

        return mask;
    }

    //выбор живого луча для управляющих и mtu зондов с учетом предпочтительного канала
    private int SelectHealthyProbeRay()
    {
        var pref = Math.Clamp(Volatile.Read(ref _preferredRay), 0, 1);
        var prefRx = pref == 0 ? Volatile.Read(ref _lastRxTicks0) : Volatile.Read(ref _lastRxTicks1);
        if (prefRx > 0)
        {
            return pref;
        }

        var alt = pref == 0 ? 1 : 0;
        var altRx = alt == 0 ? Volatile.Read(ref _lastRxTicks0) : Volatile.Read(ref _lastRxTicks1);
        return altRx > 0
            ? alt
            : Volatile.Read(ref _activeRays) > 1
                ? pref
                : 0;
    }

    //отмена ожидающих служебных зондов и подтверждений при остановке
    private void CancelPendingControlWaiters()
    {
        foreach (var kvp in _pendingMtuProbes)
        {
            if (!_pendingMtuProbes.TryRemove(kvp.Key, out var tcs))
            {
                continue;
            }

            if (tcs != null)
            {
                _ = tcs.TrySetCanceled();
            }
        }

        foreach (var kvp in _pendingMtuSyncAcks)
        {
            if (_pendingMtuSyncAcks.TryRemove(kvp.Key, out var tcs))
            {
                _ = tcs.TrySetCanceled();
            }
        }
    }
}
