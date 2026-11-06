namespace obxodka.Core.Transports;

public sealed partial class ObxodkaWebSocketTransport
{
    //фоновый цикл периодического пинга канала
    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Volatile.Read(ref _connected))
        {
            try
            {
                await Task.Delay(5000, ct).ConfigureAwait(false);
                await SendPingProbeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }
        }
    }

    //отправка исходящего пользовательского пакета из пула памяти
    public void SendPacketFromPool(byte[] packet, int length)
    {
        if (length <= 0 || !Volatile.Read(ref _connected))
        {
            ArrayPool<byte>.Shared.Return(packet);
            return;
        }

        if (!_txChannel.Writer.TryWrite(new WsTxItem(ObxodkaFraming.CmdData, packet, length)))
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

        _ = _txChannel.Writer.TryWrite(new WsTxItem(ObxodkaFraming.CmdDisconnect, [], 0));
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
        if (!_txChannel.Writer.TryWrite(new WsTxItem(ObxodkaFraming.CmdPing, pingPayload, 8)))
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
        var nowTs = Stopwatch.GetTimestamp();
        _pendingMtuProbes[nowTs] = tcs;

        var payloadLen = Math.Max(16, size - 2);
        var buf = ArrayPool<byte>.Shared.Rent(payloadLen);
        try
        {
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), nowTs);
            buf[8] = 0x11;
            RandomNumberGenerator.Fill(buf.AsSpan(9, payloadLen - 9));

            if (!_txChannel.Writer.TryWrite(new WsTxItem(ObxodkaFraming.CmdPing, buf, payloadLen)))
            {
                ArrayPool<byte>.Shared.Return(buf);
                return false;
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(TimeSpan.FromMilliseconds(1200));
            using var reg = linkedCts.Token.Register(() => tcs.TrySetResult(0));

            var returnedLen = await tcs.Task.ConfigureAwait(false);
            return returnedLen >= payloadLen;
        }
        finally
        {
            _ = _pendingMtuProbes.TryRemove(nowTs, out _);
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

        if (!_txChannel.Writer.TryWrite(new WsTxItem(ObxodkaFraming.CmdMtuBurstRequest, [], 0)))
        {
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
        if (!Volatile.Read(ref _connected))
        {
            return Task.CompletedTask;
        }

        var buf = ArrayPool<byte>.Shared.Rent(2);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), (ushort)mtu);
        if (!_txChannel.Writer.TryWrite(new WsTxItem(ObxodkaFraming.CmdMtuSync, buf, 2)))
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
        return Task.CompletedTask;
    }

    //синхронное освобождение ресурсов
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Volatile.Write(ref _connected, false);
        _ = _txChannel.Writer.TryComplete();
        while (_txChannel.Reader.TryRead(out var item))
        {
            if (item.Buffer.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(item.Buffer);
            }
        }
        _cts?.Cancel();
        _cts?.Dispose();
        _webSocket?.Dispose();
        _wsInvoker?.Dispose();
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
        _ = _txChannel.Writer.TryComplete();
        while (_txChannel.Reader.TryRead(out var item))
        {
            if (item.Buffer.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(item.Buffer);
            }
        }
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        if (_webSocket is { State: WebSocketState.Open })
        {
            try
            {
                await _webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
        }
        _webSocket?.Dispose();
        _wsInvoker?.Dispose();
        _cts?.Dispose();
    }
}
