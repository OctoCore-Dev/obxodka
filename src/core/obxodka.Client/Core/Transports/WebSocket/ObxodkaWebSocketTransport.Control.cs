namespace obxodka.Core.Transports;

public sealed partial class ObxodkaWebSocketTransport
{
    //отправка исходящего пользовательского пакета из пула памяти
    public void SendPacketFromPool(byte[] packet, int length)
    {
        if (length <= 0 || !Volatile.Read(ref _connected))
        {
            ArrayPool<byte>.Shared.Return(packet);
            return;
        }

        if (!_txQueue.TryEnqueue(packet, length))
        {
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    //отправка сигнала корректного разрыва канала
    public Task SendDisconnectSignalAsync()
    {
        if (!Volatile.Read(ref _connected))
        {
            return Task.CompletedTask;
        }

        var discBuf = ArrayPool<byte>.Shared.Rent(2);
        discBuf[0] = 0xEE;
        discBuf[1] = 0x99;
        if (!_txQueue.TryEnqueue(discBuf, 2))
        {
            ArrayPool<byte>.Shared.Return(discBuf);
        }

        return Task.CompletedTask;
    }

    //отправка эхо зонда для замера сетевого пинга
    public Task SendPingProbeAsync()
    {
        if (!Volatile.Read(ref _connected))
        {
            return Task.CompletedTask;
        }

        var pingPayload = ArrayPool<byte>.Shared.Rent(8);
        BinaryPrimitives.WriteInt64LittleEndian(pingPayload.AsSpan(0, 8), Stopwatch.GetTimestamp());
        if (!_txQueue.TryEnqueue(pingPayload, 8))
        {
            ArrayPool<byte>.Shared.Return(pingPayload);
        }

        return Task.CompletedTask;
    }

    //реальная проверка проходимости исходящего mtu заданного размера через websocket
    public async Task<bool> ProbeMtuAsync(int size, CancellationToken ct)
    {
        if (!Volatile.Read(ref _connected) || _cts?.IsCancellationRequested == true || size < 16)
        {
            return false;
        }

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeTs = Stopwatch.GetTimestamp();
        while (!_pendingMtuProbes.TryAdd(probeTs, tcs))
        {
            probeTs++;
        }

        var payloadLen = Math.Max(16, size - 2);
        var queueLen = payloadLen + 2;
        var buf = ArrayPool<byte>.Shared.Rent(queueLen);
        try
        {
            buf[0] = 0xAA;
            buf[1] = 0xBB;
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(2, 8), probeTs);
            buf[10] = 0x11;
            RandomNumberGenerator.Fill(buf.AsSpan(11, queueLen - 11));

            if (!_txQueue.TryEnqueue(buf, queueLen))
            {
                ArrayPool<byte>.Shared.Return(buf);
                return false;
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(TimeSpan.FromMilliseconds(1200));
            await using var reg = linkedCts.Token.Register(() => tcs.TrySetResult(0));

            var returnedLen = await tcs.Task.ConfigureAwait(false);
            return returnedLen >= payloadLen;
        }
        finally
        {
            _ = ((IDictionary<long, TaskCompletionSource<int>>)_pendingMtuProbes).Remove(probeTs);
        }
    }

    //запрос серии тестовых пакетов от сервера и получение результатов замера входящего mtu
    public async Task<int[]> MeasureBurstMtuAsync(CancellationToken ct = default)
    {
        if (!Volatile.Read(ref _connected) || _cts?.IsCancellationRequested == true)
        {
            return [];
        }

        lock (_receivedBurstMtus)
        {
            _receivedBurstMtus.Clear();
        }

        var burstBuf = ArrayPool<byte>.Shared.Rent(2);
        burstBuf[0] = 0xEE;
        burstBuf[1] = 0x77;
        if (!_txQueue.TryEnqueue(burstBuf, 2))
        {
            ArrayPool<byte>.Shared.Return(burstBuf);
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

    //синхронизация значения mtu с сервером
    public Task SendMtuSyncAsync(int mtu)
    {
        if (!Volatile.Read(ref _connected))
        {
            return Task.CompletedTask;
        }

        var buf = ArrayPool<byte>.Shared.Rent(4);
        buf[0] = 0xCC;
        buf[1] = 0xDD;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), (ushort)mtu);
        if (!_txQueue.TryEnqueue(buf, 4))
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        return Task.CompletedTask;
    }

    //синхронное освобождение ресурсов
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _connected, false);
        _txQueue.Dispose();
        _txQueue.DrainAndReturn(static buf => ArrayPool<byte>.Shared.Return(buf));
        _cts?.Cancel();
        _cts?.Dispose();
        _webSocket?.Dispose();
        _wsInvoker?.Dispose();
    }

    //асинхронное освобождение ресурсов
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _connected, false);
        using var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        var loopTasks = _activeLoopTasks;
        _activeLoopTasks = [];
        var loopsCompleted = loopTasks.Length == 0;
        if (loopTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(loopTasks).WaitAsync(TimeSpan.FromSeconds(1.5), CancellationToken.None).ConfigureAwait(false);
                loopsCompleted = true;
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or WebSocketException or IOException
                                           or ObjectDisposedException or InvalidOperationException)
            {
                //игнорируем таймаут ожидания завершения фоновых циклов
            }
        }

        _txQueue.DrainAndReturn(static buf => ArrayPool<byte>.Shared.Return(buf));
        _txQueue.Dispose();
        if (loopsCompleted && _webSocket is { State: WebSocketState.Open })
        {
            try
            {
                await _webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException
                                           or InvalidOperationException)
            {
                //подавление ошибки закрытия сокета при завершении
            }
        }

        _webSocket?.Dispose();
        _wsInvoker?.Dispose();
    }

    //фоновый цикл периодического пинга канала
    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Volatile.Read(ref _connected))
        {
            try
            {
                var jitteredDelayMs = RandomNumberGenerator.GetInt32(4100, 7600);
                await Task.Delay(jitteredDelayMs, ct).ConfigureAwait(false);
                await SendPingProbeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                //игнорируем сетевую ошибку отправки пинга
            }
        }
    }
}
