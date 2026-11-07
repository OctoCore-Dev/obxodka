namespace obxodka.Core.Transports;

public sealed partial class ObxodkaStreamTransport
{
    //фоновый цикл работы основного луча ray 0
    private async Task RunPrimaryRayLoopAsync(string channelHost, int initialPort, string thumbprint, bool enableDpi, CancellationToken ct)
    {
        int[] primaryPorts = initialPort == 443 ? [443, 8443] : [initialPort, 443, 8443];
        var portIdx = 0;
        var currentClientPort = initialPort;
        var consecutiveDrops = 0;
        var forceNewSocket = false;

        while (!ct.IsCancellationRequested && (Volatile.Read(ref _connected) || !_ipTcs.Task.IsCompleted))
        {
            var activePort = primaryPorts[portIdx % primaryPorts.Length];
            var client = (!forceNewSocket && activePort == currentClientPort && _httpClient0 != null)
                ? _httpClient0
                : ResetRayHttpClient(0, channelHost, activePort, enableDpi);
            forceNewSocket = false;
            currentClientPort = activePort;
            var rxBefore = Volatile.Read(ref _lastRxTicks0);
            var rotatedCleanly = false;
            try
            {
                rotatedCleanly = await RunStreamAsync(0, client, channelHost, activePort, thumbprint, ct).ConfigureAwait(false);
                forceNewSocket = rotatedCleanly;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                AppLogger.LogWarning($"[OBXODKA-STREAM] Ray #0 socket aborted on port {activePort} by health watchdog. Failing over to next port...");
                portIdx++;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (Volatile.Read(ref _connected))
                {
                    AppLogger.LogWarning($"[OBXODKA-STREAM] Ray #0 dropped on port {activePort}: {ex.Message}. Reconnecting in 1s...");
                }
                portIdx++;
            }

            if (rotatedCleanly || Volatile.Read(ref _lastRxTicks0) > rxBefore)
            {
                consecutiveDrops = 0;
            }
            else
            {
                consecutiveDrops++;
            }

            if (consecutiveDrops >= primaryPorts.Length && _rayCts[1] == null && _rayCts[2] == null && Volatile.Read(ref _connected) && !ct.IsCancellationRequested)
            {
                AppLogger.LogWarning("[OBXODKA-STREAM] All rays dropped across all ports. Signaling connection drop.");
                _inspector.RecordStep($"All rays dropped after port hopping across ports {string.Join(',', primaryPorts)}");
                _ = _inspector.ReportIncidentIfExhaustedAsync(
                    reason: "AllRaysDroppedAfterRecovery",
                    targetHost: channelHost,
                    effectivePort: activePort,
                    currentMtu: SmartMtuEngine.CurrentMtu,
                    targetIp: _activeServerIp,
                    apiBaseUrl: AppConfig.ApiBaseUrl, cancellationToken: CancellationToken.None);
                Volatile.Write(ref _connected, false);
                OnConnectionDropped?.Invoke();
                break;
            }

            if (!ct.IsCancellationRequested && Volatile.Read(ref _connected))
            {
                try
                {
                    await Task.Delay(rotatedCleanly ? 15 : 500, ct).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
            }
            else if (!_ipTcs.Task.IsCompleted)
            {
                break;
            }
        }
    }

    //фоновый цикл работы второго луча ray 1
    private async Task RunSecondaryRayAsync(string channelHost, int initialPort, string thumbprint, bool enableDpi, CancellationToken ct)
    {
        int[] secondaryPorts = initialPort == 8443 ? [8443, 443] : [initialPort, 8443, 443];
        var portIdx = 0;
        var currentClientPort = initialPort;
        var forceNewSocket = false;

        while (!ct.IsCancellationRequested && (Volatile.Read(ref _connected) || !_ipTcs.Task.IsCompleted))
        {
            var activePort = secondaryPorts[portIdx % secondaryPorts.Length];
            var client = (!forceNewSocket && activePort == currentClientPort && _httpClient1 != null)
                ? _httpClient1
                : ResetRayHttpClient(1, channelHost, activePort, enableDpi);
            forceNewSocket = false;
            currentClientPort = activePort;
            var rotatedCleanly = false;
            try
            {
                rotatedCleanly = await RunStreamAsync(1, client, channelHost, activePort, thumbprint, ct).ConfigureAwait(false);
                forceNewSocket = rotatedCleanly;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                AppLogger.LogWarning($"[OBXODKA-STREAM] Ray #1 socket aborted on port {activePort} by health watchdog. Failing over to next port...");
                portIdx++;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"[OBXODKA-STREAM] Ray #1 dropped on port {activePort}: {ex.Message}. Reconnecting in 2s...");
                portIdx++;
            }

            if (!ct.IsCancellationRequested && Volatile.Read(ref _connected))
            {
                try
                {
                    await Task.Delay(rotatedCleanly ? 15 : 500, ct).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
            }
            else if (!_ipTcs.Task.IsCompleted)
            {
                break;
            }
        }
    }

    //фоновый цикл работы третьего луча инспектора ray 2
    private async Task RunTertiaryRayAsync(string channelHost, int initialPort, string thumbprint, bool enableDpi, CancellationToken ct)
    {
        int[] tertiaryPorts = [initialPort, 2053, 8443, 443];
        var portIdx = 0;
        var currentClientPort = initialPort;
        var forceNewSocket = false;

        while (!ct.IsCancellationRequested && (Volatile.Read(ref _connected) || !_ipTcs.Task.IsCompleted))
        {
            var activePort = tertiaryPorts[portIdx % tertiaryPorts.Length];
            var client = (!forceNewSocket && activePort == currentClientPort && _httpClient2 != null)
                ? _httpClient2
                : ResetRayHttpClient(2, channelHost, activePort, enableDpi);
            forceNewSocket = false;
            currentClientPort = activePort;
            var rotatedCleanly = false;
            try
            {
                rotatedCleanly = await RunStreamAsync(2, client, channelHost, activePort, thumbprint, ct).ConfigureAwait(false);
                forceNewSocket = rotatedCleanly;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                AppLogger.LogWarning($"[OBXODKA-STREAM] Ray #2 socket aborted on port {activePort} by health watchdog. Failing over to next port...");
                portIdx++;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"[OBXODKA-STREAM] Ray #2 dropped on port {activePort}: {ex.Message}. Reconnecting in 2s...");
                portIdx++;
            }

            if (!ct.IsCancellationRequested && Volatile.Read(ref _connected))
            {
                try
                {
                    await Task.Delay(rotatedCleanly ? 15 : 500, ct).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
            }
            else if (!_ipTcs.Task.IsCompleted)
            {
                break;
            }
        }
    }

    //ядро передачи: подъем дуплексного http2 потока приема и отдачи с нарезкой сессии
    private async Task<bool> RunStreamAsync(int ray, HttpClient client, string channelHost, int port, string thumbprint, CancellationToken ct)
    {
        using var rayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _rayCts[ray] = rayCts;
        var token = rayCts.Token;
        var rotatedCleanly = false;
        long raySharedBytes = 0;

        try
        {
            var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var cycleIdx = Interlocked.Increment(ref _rayCycleCount[ray]);
            var streamPath = SniEngine.GetStreamRoute(ray, cycleIdx, out var requestContentType);
            var sessionSeed = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
            var candidateSessionKey = ObxodkaFraming.DeriveSessionKey(thumbprint, ray, sessionSeed);
            var activeSessionKey = 0u;
            var cid = RandomNumberGenerator.GetInt32(10000000, 99999999);
            var requestUri = $"https://{channelHost}:{port}{streamPath}?cid={cid}&_t={nowSec}&v=133.0";
            using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };

            RoutingHello.ApplyBrowserHeaders(request, ActiveBrowserProfile);

            var duplexContent = new PushStreamContent(async (stream, pushToken) =>
            {
                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(token, pushToken);
                var pumpToken = pumpCts.Token;
                var coalesceBuf = ArrayPool<byte>.Shared.Rent(65536);

                try
                {
                    var hsProof = ObxodkaFraming.ComputeHandshakeProof(thumbprint, ray, sessionSeed, nowSec);
                    var handshakeStr = string.IsNullOrEmpty(_assignedIp)
                        ? $"{thumbprint}|R:{ray}|M:{SmartMtuEngine.CurrentMtu}|K:{sessionSeed:X8}|T:{nowSec}|P:{hsProof}"
                        : $"{thumbprint}|{_assignedIp}|R:{ray}|M:{SmartMtuEngine.CurrentMtu}|K:{sessionSeed:X8}|T:{nowSec}|P:{hsProof}";
                    var hsPayload = Encoding.UTF8.GetBytes(handshakeStr);
                    var hsFrame = ObxodkaFraming.Pack(ObxodkaFraming.CmdHandshake, hsPayload, out var hsTotalLen);
                    try
                    {
                        await stream.WriteAsync(hsFrame.AsMemory(0, hsTotalLen), pumpToken).ConfigureAwait(false);
                        var chaffFrame = ObxodkaFraming.Pack(ObxodkaFraming.CmdChaff, [], out var chaffLen, SmartMtuEngine.CurrentMtu, candidateSessionKey);
                        try
                        {
                            await stream.WriteAsync(chaffFrame.AsMemory(0, chaffLen), pumpToken).ConfigureAwait(false);
                            hsTotalLen += chaffLen;
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(chaffFrame);
                        }
                        await stream.FlushAsync(pumpToken).ConfigureAwait(false);
                        _ = Interlocked.Add(ref raySharedBytes, hsTotalLen);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(hsFrame);
                    }

                    var txQueue = _txChannels[ray];
                    long rayTxBytes = hsTotalLen;
                    while (!pumpToken.IsCancellationRequested)
                    {
                        var firstItem = await txQueue.DequeueAsync(pumpToken).ConfigureAwait(false);
                        var coalescedLen = 0;
                        var batchCount = 0;
                        var item = firstItem;
                        var hasItem = true;

                        while (hasItem)
                        {
                            var (packet, length) = item;
                            if (packet != null && length > 0)
                            {
                                try
                                {
                                    var isMtu = length == 4 && packet[0] == 0xCC && packet[1] == 0xDD;
                                    var isBurstReq = length == 2 && packet[0] == 0xEE && packet[1] == 0x77;
                                    var isDisconnect = length == 2 && packet[0] == 0xEE && packet[1] == 0x99;
                                    var isPing = (length >= 10 && packet[0] == 0xAA && packet[1] == 0xBB) || length == 8;
                                    var cmd = isMtu
                                        ? ObxodkaFraming.CmdMtuSync
                                        : (isBurstReq
                                            ? ObxodkaFraming.CmdMtuBurstRequest
                                            : (isDisconnect
                                                ? ObxodkaFraming.CmdDisconnect
                                                : (isPing ? ObxodkaFraming.CmdPing : ObxodkaFraming.CmdData)));
                                    var payloadOffset = isMtu
                                        ? 2
                                        : ((isBurstReq || isDisconnect)
                                            ? 0
                                            : ((length >= 10 && packet[0] == 0xAA && packet[1] == 0xBB) ? 2 : 0));
                                    var payloadLength = isMtu
                                        ? 2
                                        : ((isBurstReq || isDisconnect)
                                            ? 0
                                            : ((length >= 10 && packet[0] == 0xAA && packet[1] == 0xBB) ? length - 2 : length));

                                    var isEarly = (rayTxBytes + coalescedLen) < ObxodkaFraming.EarlyStageWindowBytes;
                                    var paddingLen = ObxodkaFraming.CalculatePaddingLength(cmd, payloadLength, maxMtu: SmartMtuEngine.CurrentMtu, isEarlySession: isEarly);
                                    var totalLen = ObxodkaFraming.HeaderSize + payloadLength + paddingLen;

                                    if (coalescedLen + totalLen > coalesceBuf.Length && coalescedLen > 0)
                                    {
                                        await stream.WriteAsync(coalesceBuf.AsMemory(0, coalescedLen), pumpToken).ConfigureAwait(false);
                                        rayTxBytes += coalescedLen;
                                        _ = Interlocked.Add(ref raySharedBytes, coalescedLen);
                                        coalescedLen = 0;
                                    }

                                    var txKey = Volatile.Read(ref activeSessionKey);
                                    if (totalLen <= coalesceBuf.Length)
                                    {
                                        ObxodkaFraming.WriteFrame(coalesceBuf.AsSpan(coalescedLen, totalLen), cmd, packet.AsSpan(payloadOffset, payloadLength), paddingLen, txKey);
                                        coalescedLen += totalLen;
                                    }
                                    else
                                    {
                                        var frame = ArrayPool<byte>.Shared.Rent(totalLen);
                                        try
                                        {
                                            ObxodkaFraming.WriteFrame(frame.AsSpan(0, totalLen), cmd, packet.AsSpan(payloadOffset, payloadLength), paddingLen, txKey);
                                            await stream.WriteAsync(frame.AsMemory(0, totalLen), pumpToken).ConfigureAwait(false);
                                            rayTxBytes += totalLen;
                                            _ = Interlocked.Add(ref raySharedBytes, totalLen);
                                        }
                                        finally
                                        {
                                            ArrayPool<byte>.Shared.Return(frame);
                                        }
                                    }
                                }
                                finally
                                {
                                    ArrayPool<byte>.Shared.Return(packet);
                                }
                            }

                            batchCount++;
                            hasItem = batchCount < 128 && txQueue.TryDequeue(out item);
                        }

                        if (coalescedLen > 0)
                        {
                            await stream.WriteAsync(coalesceBuf.AsMemory(0, coalescedLen), pumpToken).ConfigureAwait(false);
                            rayTxBytes += coalescedLen;
                            _ = Interlocked.Add(ref raySharedBytes, coalescedLen);
                        }

                        await stream.FlushAsync(pumpToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"[OBXODKA-TX] Ray #{ray} upload pump stopped: {ex.Message}");
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(coalesceBuf);
                }
            });

            duplexContent.Headers.ContentType = MediaTypeHeaderValue.Parse(requestContentType);
            request.Content = duplexContent;

            AppLogger.Log($"[H2 STREAM] Ray #{ray} initiating duplex tunnel POST to https://{channelHost}:{port}{streamPath} (MTU={SmartMtuEngine.CurrentMtu}, MIME={requestContentType}, Browser={ActiveBrowserProfile})...");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            AppLogger.Log($"[H2 RESPONSE] Ray #{ray} received HTTP {(int)response.StatusCode} {response.ReasonPhrase} (Version={response.Version}).");

            if (!response.IsSuccessStatusCode)
            {
                var rawErr = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                AppLogger.LogError($"[H2 ERROR] Ray #{ray} server rejected stream: HTTP {(int)response.StatusCode} - {rawErr}");
                _ = response.EnsureSuccessStatusCode();
            }

            using var responseStream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var rxBuf = ArrayPool<byte>.Shared.Rent(131072);
            var rxFilled = 0;
            var rxOffset = 0;

            try
            {
                var streamActive = true;
                var streamStartedTicks = Stopwatch.GetTimestamp();
                SniEngine.CalculateStreamSliceWindow(ray, out var maxStreamDurationTicks, out var maxSliceBytes);
                while (!token.IsCancellationRequested && streamActive)
                {
                    var elapsedTicks = Stopwatch.GetTimestamp() - streamStartedTicks;
                    var transferredBytes = Volatile.Read(ref raySharedBytes);
                    if ((elapsedTicks > maxStreamDurationTicks || transferredBytes >= maxSliceBytes) && Volatile.Read(ref _activeRays) >= 2)
                    {
                        var siblingRay = ray == 2 ? 0 : (ray + 1) % 2;
                        var siblingLastRx = siblingRay == 0 ? Volatile.Read(ref _lastRxTicks0) : Volatile.Read(ref _lastRxTicks1);
                        if (siblingLastRx > 0 && (Stopwatch.GetTimestamp() - siblingLastRx) < 8 * Stopwatch.Frequency)
                        {
                            if (ray is 0 or 1 && Volatile.Read(ref _preferredRay) == ray)
                            {
                                Volatile.Write(ref _preferredRay, siblingRay);
                            }

                            rotatedCleanly = true;
                            AppLogger.Log($"[OBXODKA-STEALTH] Session slice complete on Ray #{ray} ({transferredBytes / 1024}KB) -> rotating to next stealth chunk.");
                            await rayCts.CancelAsync().ConfigureAwait(false);
                            break;
                        }
                    }

                    var bytesRead = await responseStream.ReadAsync(rxBuf.AsMemory(rxFilled, rxBuf.Length - rxFilled), token).ConfigureAwait(false);
                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    rxFilled += bytesRead;
                    _ = Interlocked.Add(ref raySharedBytes, bytesRead);

                    while (rxFilled - rxOffset >= ObxodkaFraming.HeaderSize)
                    {
                        var headerSpan = rxBuf.AsSpan(rxOffset, ObxodkaFraming.HeaderSize);
                        var decodeKey = Volatile.Read(ref activeSessionKey);
                        if (decodeKey == 0 && candidateSessionKey != 0 &&
                            ObxodkaFraming.TryDecodeHeader(headerSpan, out var candTotal, out _, out var candCmd, out _, candidateSessionKey) &&
                            candCmd == ObxodkaFraming.CmdHandshakeResponse && candTotal <= 1024)
                        {
                            decodeKey = candidateSessionKey;
                            Volatile.Write(ref activeSessionKey, candidateSessionKey);
                        }
                        if (!ObxodkaFraming.TryDecodeHeader(headerSpan, out var totalLen, out var payloadLen, out var command, out var salt, decodeKey))
                        {
                            if (decodeKey == 0 && ObxodkaFraming.TryDecodeHeader(headerSpan, out totalLen, out payloadLen, out command, out salt, candidateSessionKey))
                            {
                                decodeKey = candidateSessionKey;
                                Volatile.Write(ref activeSessionKey, candidateSessionKey);
                            }
                            else
                            {
                                AppLogger.LogError($"[OBXODKA-STREAM] Ray #{ray} desync: invalid frame header.");
                                streamActive = false;
                                break;
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

                        _inspector.RecordRx(ray, Stopwatch.GetTimestamp());
                        if (ray == 0)
                        {
                            Volatile.Write(ref _lastRxTicks0, Stopwatch.GetTimestamp());
                        }
                        else if (ray == 1)
                        {
                            Volatile.Write(ref _lastRxTicks1, Stopwatch.GetTimestamp());
                        }
                        else if (ray == 2)
                        {
                            Volatile.Write(ref _lastRxTicks2, Stopwatch.GetTimestamp());
                        }

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
                                Volatile.Write(ref _connected, true);
                                var isFirst = _ipTcs.TrySetResult((assignedIp, assignedIpV6));

                                if (isFirst)
                                {
                                    Volatile.Write(ref _connectedAtTicks, Stopwatch.GetTimestamp());
                                    //пользовательский preferredRay может быть только 0 или 1 (ray 2 зарезервирован под control plane)
                                    Volatile.Write(ref _preferredRay, ray is 0 or 1 ? ray : 0);
                                    EffectivePort = port;
                                    AppLogger.Log($"[OBXODKA-ESTABLISHED] Tunnel online via Ray #{ray} (Port {port}). IPv4={assignedIp}, IPv6={assignedIpV6}, MTU={SmartMtuEngine.CurrentMtu}");
                                    if (Interlocked.Exchange(ref _pingLoopStarted, 1) == 0)
                                    {
                                        _ = Task.Run(() => PingLoopAsync(_cts!.Token), _cts!.Token);
                                    }
                                }
                                else
                                {
                                    Volatile.Write(ref _activeRays, Math.Max(ray + 1, Volatile.Read(ref _activeRays)));
                                    AppLogger.Log($"[OBXODKA-STREAM] Ray #{ray} online and synchronized on port {port}. Active rays = {Volatile.Read(ref _activeRays)}.");
                                }
                            }
                        }
                        else if (command == ObxodkaFraming.CmdData)
                        {
                            if (ray == 2)
                            {
                                //ray 2 зарезервирован под инспектор телеметрии, пользовательские данные здесь запрещены
                                continue;
                            }
                            _inspector.RecordDataRx(payloadLen);
                            if (payloadLen >= 20 && !_deduplicator.IsDuplicate(payloadSpan))
                            {
                                var rented = ArrayPool<byte>.Shared.Rent(payloadLen);
                                payloadSpan.CopyTo(rented);
                                OnPacketReceived?.Invoke(rented, payloadLen);
                            }
                        }
                        else if (command == ObxodkaFraming.CmdPong)
                        {
                            var tsSpan = (payloadLen >= 10 && payloadSpan[0] == 0xAA && payloadSpan[1] == 0xBB)
                                ? payloadSpan.Slice(2, 8)
                                : (payloadLen >= 8 ? payloadSpan[..8] : default);

                            if (!tsSpan.IsEmpty)
                            {
                                var sendTs = BinaryPrimitives.ReadInt64LittleEndian(tsSpan);
                                var now = Stopwatch.GetTimestamp();
                                var elapsedMs = (long)((now - sendTs) * 1000.0 / Stopwatch.Frequency);
                                if (elapsedMs is >= 0 and < 5000)
                                {
                                    _inspector.RecordPong(ray, sendTs, now);
                                    if (ray == 0)
                                    {
                                        var prev = Volatile.Read(ref _rayRtt0);
                                        Volatile.Write(ref _rayRtt0, prev <= 0 ? elapsedMs : ((prev * 3) + elapsedMs) / 4);
                                    }
                                    else if (ray == 1)
                                    {
                                        var prev = Volatile.Read(ref _rayRtt1);
                                        Volatile.Write(ref _rayRtt1, prev <= 0 ? elapsedMs : ((prev * 3) + elapsedMs) / 4);
                                    }
                                    else if (ray == 2)
                                    {
                                        var prev = Volatile.Read(ref _rayRtt2);
                                        Volatile.Write(ref _rayRtt2, prev <= 0 ? elapsedMs : ((prev * 3) + elapsedMs) / 4);
                                    }

                                    OnPingUpdated?.Invoke(elapsedMs);
                                }

                                if (_pendingMtuProbes.TryRemove(sendTs, out var probeTcs))
                                {
                                    _ = probeTcs.TrySetResult(payloadLen);
                                }
                            }
                        }
                        else if (command == ObxodkaFraming.CmdMtuSync)
                        {
                            if (payloadLen >= 2)
                            {
                                var ackMtu = (int)BinaryPrimitives.ReadUInt16BigEndian(payloadSpan[..2]);
                                AppLogger.Log($"[OBXODKA-STREAM] Server confirmed locked MTU {ackMtu}B");
                            }
                        }
                        else if (command == ObxodkaFraming.CmdMtuBurstPayload)
                        {
                            if (payloadLen >= 4 && payloadSpan[0] == 0xEE && payloadSpan[1] == 0x55)
                            {
                                var targetMtu = (int)BinaryPrimitives.ReadUInt16BigEndian(payloadSpan.Slice(2, 2));
                                lock (_receivedBurstMtus)
                                {
                                    _receivedBurstMtus.Add(targetMtu);
                                }
                            }
                        }
                        else if (command == ObxodkaFraming.CmdDisconnect)
                        {
                            AppLogger.Log($"[OBXODKA-STREAM] Server signaled disconnect on Ray #{ray}.");
                            streamActive = false;
                            break;
                        }
                    }

                    if (rxOffset > 0)
                    {
                        var remaining = rxFilled - rxOffset;
                        if (remaining > 0)
                        {
                            Buffer.BlockCopy(rxBuf, rxOffset, rxBuf, 0, remaining);
                        }
                        rxFilled = remaining;
                        rxOffset = 0;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rxBuf);
            }

            return rotatedCleanly;
        }
        catch (OperationCanceledException) when (rotatedCleanly)
        {
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[OBXODKA-STREAM ERROR] Ray #{ray} stream terminated: {ex.Message}");
            throw;
        }
        finally
        {
            _rayCts[ray] = null;
            var otherRay = ray == 0 ? 1 : 0;
            for (var c = 0; c < 3; c++)
            {
                if (c != ray && _rayCts[c] != null)
                {
                    otherRay = c;
                    break;
                }
            }

            var isOtherAlive = _rayCts[otherRay] != null;
            if (isOtherAlive && Volatile.Read(ref _connected) && _cts?.IsCancellationRequested != true)
            {
                if (Volatile.Read(ref _preferredRay) == ray && otherRay is 0 or 1)
                {
                    Volatile.Write(ref _preferredRay, otherRay);
                    AppLogger.LogWarning($"[OBXODKA-STREAM] Ray #{ray} offline. Migrated preferred ray to #{otherRay}.");
                }

                while (_txChannels[ray].TryDequeue(out var leftover))
                {
                    if (!_txChannels[otherRay].TryEnqueue(leftover.buffer, leftover.length))
                    {
                        ArrayPool<byte>.Shared.Return(leftover.buffer);
                    }
                }
            }
        }
    }

    //пользовательский контент для потоковой передачи в http2 duplex post
    private sealed class PushStreamContent(Func<Stream, CancellationToken, Task> onSerialize) : HttpContent
    {
        //асинхронная сериализация в поток с токеном отмены
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            onSerialize(stream, cancellationToken);

        //асинхронная сериализация в поток без токена
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        //вычисление длины контента (всегда false для потока)
        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
