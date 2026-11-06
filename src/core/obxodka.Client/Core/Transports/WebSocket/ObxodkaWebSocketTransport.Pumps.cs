namespace obxodka.Core.Transports;

public sealed partial class ObxodkaWebSocketTransport
{
    //фоновый цикл чтения входящих фреймов из websocket
    private async Task ReaderLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var rxBuf = ArrayPool<byte>.Shared.Rent(131072);
        var rxFilled = 0;
        var rxOffset = 0;

        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var receiveResult = await ws.ReceiveAsync(rxBuf.AsMemory(rxFilled, rxBuf.Length - rxFilled), ct).ConfigureAwait(false);
                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                rxFilled += receiveResult.Count;

                while (rxFilled - rxOffset >= ObxodkaFraming.HeaderSize)
                {
                    var headerSpan = rxBuf.AsSpan(rxOffset, ObxodkaFraming.HeaderSize);
                    var decodeKey = Volatile.Read(ref _sessionKey);
                    if (decodeKey == 0 && _candidateSessionKey != 0 &&
                        ObxodkaFraming.TryDecodeHeader(headerSpan, out var candTotal, out _, out var candCmd, out _, _candidateSessionKey) &&
                        candCmd == ObxodkaFraming.CmdHandshakeResponse && candTotal <= 1024)
                    {
                        decodeKey = _candidateSessionKey;
                        Volatile.Write(ref _sessionKey, _candidateSessionKey);
                    }
                    if (!ObxodkaFraming.TryDecodeHeader(headerSpan, out var totalLen, out var payloadLen, out var command, out var salt, decodeKey))
                    {
                        if (decodeKey == 0 && ObxodkaFraming.TryDecodeHeader(headerSpan, out totalLen, out payloadLen, out command, out salt, _candidateSessionKey))
                        {
                            decodeKey = _candidateSessionKey;
                            Volatile.Write(ref _sessionKey, _candidateSessionKey);
                        }
                        else
                        {
                            AppLogger.LogError("[OBXODKA-WS] Frame header decode error, closing.");
                            return;
                        }
                    }

                    if (rxFilled - rxOffset < totalLen)
                    {
                        break;
                    }

                    var payloadSpan = rxBuf.AsSpan(rxOffset + ObxodkaFraming.HeaderSize, payloadLen);
                    if (command != ObxodkaFraming.CmdChaff && payloadLen > 0)
                    {
                        ObxodkaFraming.UnmaskPayload(payloadSpan, salt, decodeKey);
                    }
                    rxOffset += totalLen;

                    if (command == ObxodkaFraming.CmdHandshakeResponse)
                    {
                        var info = Encoding.UTF8.GetString(payloadSpan);
                        if (info.StartsWith("IP:", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = info[3..].Split('|');
                            var assignedIp = parts[0];
                            var assignedIpV6 = parts.Length > 1 ? parts[1] : string.Empty;
                            foreach (var part in parts)
                            {
                                if (part.StartsWith("MTU:", StringComparison.OrdinalIgnoreCase) &&
                                    int.TryParse(part[4..], out var sMtu) &&
                                    sMtu >= SmartMtuEngine.MinMtu && sMtu <= SmartMtuEngine.MaxMtu)
                                {
                                    SmartMtuEngine.CurrentMtu = sMtu;
                                }
                            }
                            _assignedIp = assignedIp;
                            _ = _ipTcs.TrySetResult((assignedIp, assignedIpV6));
                        }
                    }
                    else if (command == ObxodkaFraming.CmdData)
                    {
                        var packetBuf = ArrayPool<byte>.Shared.Rent(payloadLen);
                        payloadSpan.CopyTo(packetBuf);
                        OnPacketReceived?.Invoke(packetBuf, payloadLen);
                    }
                    else if (command == ObxodkaFraming.CmdPing)
                    {
                        if (payloadLen >= 8)
                        {
                            var echoBytes = ArrayPool<byte>.Shared.Rent(payloadLen);
                            payloadSpan.CopyTo(echoBytes);
                            if (!_txChannel.Writer.TryWrite(new WsTxItem(ObxodkaFraming.CmdPong, echoBytes, payloadLen)))
                            {
                                ArrayPool<byte>.Shared.Return(echoBytes);
                            }
                        }
                    }
                    else if (command == ObxodkaFraming.CmdPong)
                    {
                        var tsSpan = (payloadLen >= 10 && payloadSpan[0] == 0xAA && payloadSpan[1] == 0xBB)
                            ? payloadSpan.Slice(2, 8)
                            : (payloadLen >= 8 ? payloadSpan[..8] : default);

                        if (!tsSpan.IsEmpty)
                        {
                            var sentTicks = BinaryPrimitives.ReadInt64LittleEndian(tsSpan);
                            var rtt = (Stopwatch.GetTimestamp() - sentTicks) * 1000 / Stopwatch.Frequency;
                            if (rtt is >= 0 and < 5000)
                            {
                                OnPingUpdated?.Invoke(rtt);
                            }

                            if (_pendingMtuProbes.TryRemove(sentTicks, out var probeTcs))
                            {
                                _ = probeTcs.TrySetResult(payloadLen);
                            }
                        }
                    }
                    else if (command == ObxodkaFraming.CmdMtuSync)
                    {
                        if (payloadLen >= 2)
                        {
                            var sMtu = BinaryPrimitives.ReadUInt16BigEndian(payloadSpan);
                            if (sMtu is >= SmartMtuEngine.MinMtu and <= SmartMtuEngine.MaxMtu)
                            {
                                SmartMtuEngine.CurrentMtu = sMtu;
                                OnMtuUpdated?.Invoke(sMtu);
                            }
                        }
                    }
                    else if (command == ObxodkaFraming.CmdMtuBurstPayload)
                    {
                        if (payloadLen >= 4 && payloadSpan[0] == 0xEE && payloadSpan[1] == 0x55)
                        {
                            var targetMtu = BinaryPrimitives.ReadUInt16BigEndian(payloadSpan.Slice(2, 2));
                            lock (_receivedBurstMtus)
                            {
                                if (!_receivedBurstMtus.Contains(targetMtu))
                                {
                                    _receivedBurstMtus.Add(targetMtu);
                                }
                            }
                        }
                    }
                }

                if (rxOffset > 0)
                {
                    var remaining = rxFilled - rxOffset;
                    if (remaining > 0)
                    {
                        rxBuf.AsSpan(rxOffset, remaining).CopyTo(rxBuf);
                    }
                    rxFilled = remaining;
                    rxOffset = 0;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[OBXODKA-WS] Reader loop terminated: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rxBuf);
            if (Volatile.Read(ref _connected))
            {
                Volatile.Write(ref _connected, false);
                OnConnectionDropped?.Invoke();
            }
        }
    }

    //фоновый цикл отправки исходящих пакетов через единую очередь сокета
    private async Task WriterLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var coalesceBuf = ArrayPool<byte>.Shared.Rent(65536);
        var reader = _txChannel.Reader;

        try
        {
            long wsTxBytes = 0;
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                var coalescedLen = 0;

                while (reader.TryRead(out var item))
                {
                    var cmd = item.Command;
                    var packet = item.Buffer;
                    var length = item.Length;

                    if (packet == null || (length <= 0 && cmd is not (ObxodkaFraming.CmdDisconnect or ObxodkaFraming.CmdMtuBurstRequest)))
                    {
                        if (packet != null && packet.Length > 0)
                        {
                            ArrayPool<byte>.Shared.Return(packet);
                        }
                        continue;
                    }

                    try
                    {
                        var isEarly = (wsTxBytes + coalescedLen) < ObxodkaFraming.EarlyStageWindowBytes;
                        var paddingLen = ObxodkaFraming.CalculatePaddingLength(cmd, length, maxMtu: SmartMtuEngine.CurrentMtu, isEarlySession: isEarly);
                        var totalLen = ObxodkaFraming.HeaderSize + length + paddingLen;

                        if (coalescedLen + totalLen > coalesceBuf.Length && coalescedLen > 0)
                        {
                            await ws.SendAsync(coalesceBuf.AsMemory(0, coalescedLen), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                            wsTxBytes += coalescedLen;
                            coalescedLen = 0;
                        }

                        if (totalLen <= coalesceBuf.Length)
                        {
                            ObxodkaFraming.WriteFrame(coalesceBuf.AsSpan(coalescedLen, totalLen), cmd, packet.AsSpan(0, length), paddingLen, _sessionKey);
                            coalescedLen += totalLen;
                        }
                        else
                        {
                            var frame = ArrayPool<byte>.Shared.Rent(totalLen);
                            try
                            {
                                ObxodkaFraming.WriteFrame(frame.AsSpan(0, totalLen), cmd, packet.AsSpan(0, length), paddingLen, _sessionKey);
                                await ws.SendAsync(frame.AsMemory(0, totalLen), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                                wsTxBytes += totalLen;
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(frame);
                            }
                        }
                    }
                    finally
                    {
                        if (packet.Length > 0)
                        {
                            ArrayPool<byte>.Shared.Return(packet);
                        }
                    }
                }

                if (coalescedLen > 0)
                {
                    await ws.SendAsync(coalesceBuf.AsMemory(0, coalescedLen), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                    wsTxBytes += coalescedLen;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[OBXODKA-WS] Writer loop terminated: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(coalesceBuf);
        }
    }
}
