using obxodka.Shared.Stealth.Tls13;

namespace obxodka.Core.Transports;

//прямой стелс-транспорт obx-v9 reality hydra поверх собственного стека rfc 8446 tls 1.3 chromium
//с мультипортовым входом (443/8443/2053), проверкой 24 кб трафика и каруселью коротких сокетов (<12 кб) против заморозки тспу
public sealed class ObxodkaRealityTransport(
    int serverPort = 443,
    string? configuredCoverSni = null,
    string? preferredAssignedIp = null,
    bool forceHydraCarousel = false)
    : IVpnTransport, IHealthAwareTransport, IAntiFreezeTransport
{
    private const int HydraServerBudgetBytes = 11200;
    private const int HydraClientMinBudgetBytes = 9400;
    private const int HydraClientMaxBudgetBytes = 11400;

    //нейтральные европейские и глобальные домены-доноры без упоминания бренда vpn
    private static readonly string[] s_coverDonorHosts =
    [
        "cdn.jsdelivr.net",
        "fastly.jsdelivr.net",
        "www.wikipedia.org",
        "unpkg.com"
    ];

    private readonly string? _configuredCoverSni = configuredCoverSni;
    private readonly SemaphoreSlim _handoffGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<int>> _pendingMtuProbes = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _pendingMtuSyncAcks = new();
    private readonly List<int> _receivedBurstMtus = [];
    private readonly int _serverPort = serverPort > 0 ? serverPort : 443;
    private readonly PriorityPacketQueue _txQueue = new(8192);
    private readonly Channel<WarmRealitySocket> _warmSockets = Channel.CreateBounded<WarmRealitySocket>(2);

    private volatile RealityLeg? _activeLeg;
    private Task[] _activeLoopTasks = [];
    private string _activeServerIp = string.Empty;
    private volatile string? _assignedIp = preferredAssignedIp;
    private string _clientThumbprint = string.Empty;
    private bool _connected;
    private string[] _coverCandidates = s_coverDonorHosts;
    private CancellationTokenSource? _cts;
    private int _disposed;
    private int _dropSignaled;
    private int _hydraEnabled = forceHydraCarousel ? 1 : 0;
    private int _hydraLegCounter;
    private TaskCompletionSource<(string ip, string ip6)> _ipTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int[] _portCandidates = [443, 8443, 2053];
    private Action<Socket>? _protectAction;
    private byte[] _realityPub = [];
    private string _spkiPin = string.Empty;
    private IPAddress? _targetIp;
    private Task? _warmProducerTask;

    public string ActiveSni { get; private set; } = string.Empty;
    public TlsBrowserProfile ActiveBrowserProfile { get; private set; } = TlsBrowserProfile.Chrome;
    public static Action<Socket>? OnSocketCreated { get; set; }

    //счетчик совершенных ротаций коротких сокетов hydra в текущей сессии
    public int HydraRotationsCount => Volatile.Read(ref _hydraLegCounter);

    public bool IsHydraCarouselActive => Volatile.Read(ref _hydraEnabled) != 0;

    public string? RemoteConnectedEndpointIp => string.IsNullOrWhiteSpace(_activeServerIp) ? null : _activeServerIp;

    public void ReportUnhealthyEndpoint() => OctopusEngine.MarkNodeByteCapped(_activeServerIp);

    public string ProtocolName => "OBXODKA-REALITY";

    public bool IsConnected => Volatile.Read(ref _connected);

    public event Action<byte[], int>? OnPacketReceived;
    public event Action<long>? OnPingUpdated;
    public event Action? OnConnectionDropped;
    public event Action<int>? OnMtuUpdated;

    public void ProtectSockets(Action<Socket> protectAction) => _protectAction = protectAction;

    [SuppressMessage("Reliability", "CA2000:Ликвидировать объекты перед потерей области",
        Justification = "Владение потоком и звеном передается в _activeLeg либо гарантированно освобождается в CleanupFailedAttemptAsync")]
    public async Task<(string ip, string ip6)> ConnectAsync(string serverIp, string thumbprint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        _activeServerIp = serverIp;
        _clientThumbprint = thumbprint;
        _spkiPin = !string.IsNullOrWhiteSpace(OctopusEngine.DynamicSslPublicKeyHash)
            ? OctopusEngine.DynamicSslPublicKeyHash
            : !string.IsNullOrWhiteSpace(OctopusEngine.DirectOriginCertHash)
                ? OctopusEngine.DirectOriginCertHash
                : AppSecrets.DirectServerSpkiPin;

        _realityPub = ObxRealityAuth.DeriveNodeRealityPublicKey(_spkiPin);
        _coverCandidates = BuildCoverSniList(_configuredCoverSni);
        _portCandidates = _serverPort == 443 ? [443, 8443, 2053] : [_serverPort];

        List<(string CoverSni, int Port)> endpoints = [];
        foreach (var port in _portCandidates)
        {
            foreach (var cover in _coverCandidates)
            {
                endpoints.Add((cover, port));
            }
        }

        Exception? lastEx = null;

        for (var idx = 0; idx < endpoints.Count; idx++)
        {
            var (coverSni, candidatePort) = endpoints[idx];
            ActiveSni = coverSni;
            Volatile.Write(ref _dropSignaled, 0);
            _cts = new CancellationTokenSource();
            _ipTcs = new TaskCompletionSource<(string ip, string ip6)>(TaskCreationOptions.RunContinuationsAsynchronously);

            RealityLeg? initialLeg = null;
            var ownershipTransferred = false;

            try
            {
                using var attemptCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(3800));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token, attemptCts.Token);

                _targetIp = IPAddress.TryParse(serverIp, out var parsed)
                    ? parsed
                    : await ApiService.ResolveHostAntiPoisonAsync(serverIp, linkedCts.Token).ConfigureAwait(false);
                ActiveBrowserProfile = RoutingHello.GetProfileForAttempt(idx);

                var warm = await OpenRawTlsSocketAsync(_targetIp, candidatePort, coverSni, linkedCts.Token).ConfigureAwait(false);
                initialLeg = await PromoteWarmSocketToLegAsync(warm, IsHydraCarouselActive, linkedCts.Token).ConfigureAwait(false);
                _activeLeg = initialLeg;
                ownershipTransferred = true;

                var writerTask = Task.Run(() => WriterLoopAsync(_cts.Token), _cts.Token);
                _activeLoopTasks = [initialLeg.ReaderTask, writerTask];

                await using (linkedCts.Token.Register(() => _ = _ipTcs.TrySetCanceled()))
                {
                    var assigned = await _ipTcs.Task.ConfigureAwait(false);
                    Volatile.Write(ref _connected, true);
                    var pingTask = Task.Run(() => PingLoopAsync(_cts.Token), _cts.Token);
                    if (IsHydraCarouselActive)
                    {
                        EnsureWarmStandbyProducerStarted(_cts.Token);
                    }

                    _activeLoopTasks = [writerTask, pingTask];
                    AppLogger.Log(
                        $"[OBXODKA-REALITY] Connected to {serverIp}:{candidatePort} (CoverSNI='{coverSni}', Hydra={IsHydraCarouselActive})! Assigned IPv4={assigned.ip}");
                    return assigned;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && idx + 1 < endpoints.Count)
            {
                lastEx = ex;
                AppLogger.LogWarning($"[OBXODKA-REALITY] Candidate '{coverSni}:{candidatePort}' failed: {ex.Message}");
                await CleanupFailedAttemptAsync(initialLeg, ownershipTransferred).ConfigureAwait(false);
            }
            catch
            {
                await CleanupFailedAttemptAsync(initialLeg, ownershipTransferred).ConfigureAwait(false);
                throw;
            }
        }

        throw lastEx ?? new TimeoutException("Reality connection failed across cover SNI and port candidates.");
    }

    //проверка прохождения >20 кб трафика и автоматическое включение карусели hydra при заморозке 16 кб тспу
    public async Task<bool> VerifyThroughputOrActivateHydraAsync(CancellationToken ct)
    {
        if (!IsConnected || Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        if (IsHydraCarouselActive)
        {
            return await RunBulkEchoProbeAsync(probeCount: 20, payloadSize: 1120, timeoutMs: 2800, ct).ConfigureAwait(false) >= 16;
        }

        var passedSingle = await RunBulkEchoProbeAsync(probeCount: 18, payloadSize: 1150, timeoutMs: 1600, ct).ConfigureAwait(false);
        if (passedSingle >= 14)
        {
            return true;
        }

        AppLogger.LogWarning(
            $"[OBX-HYDRA] Single socket stalled at {passedSingle}/18 echo frames (~{passedSingle * 1150 / 1024} KB) — TSPU 16KB TCP freeze detected! Activating Sub-12KB Hydra Carousel...");

        await EnableHydraCarouselAsync(ct).ConfigureAwait(false);
        var passedHydra = await RunBulkEchoProbeAsync(probeCount: 22, payloadSize: 1120, timeoutMs: 3600, ct).ConfigureAwait(false);
        var ok = passedHydra >= 18;
        AppLogger.Log(
            $"[OBX-HYDRA] Post-activation 25KB verification: {passedHydra}/22 frames delivered across {HydraRotationsCount} rotated leg(s) (Result={(ok ? "PASS" : "FAIL")}).");
        return ok;
    }

    //принудительное включение карусели коротких сокетов hydra (<12 кб на сокет) на живой сессии без смены tun ip
    public async Task EnableHydraCarouselAsync(CancellationToken ct = default)
    {
        _ = Interlocked.Exchange(ref _hydraEnabled, 1);
        if (_cts is { IsCancellationRequested: false })
        {
            EnsureWarmStandbyProducerStarted(_cts.Token);
            await RotateActiveLegAsync(ct).ConfigureAwait(false);
        }
    }

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

    public async Task SendDisconnectSignalAsync()
    {
        if (!Volatile.Read(ref _connected) || _activeLeg is not { } leg)
        {
            return;
        }

        var frame = ObxodkaFraming.Pack(ObxodkaFraming.CmdDisconnect, [], out var frameLen, SmartMtuEngine.DefaultMtu, leg.SessionKey);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await leg.Stream.WriteAsync(frame.AsMemory(0, frameLen), cts.Token).ConfigureAwait(false);
            await leg.Stream.FlushAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            //канал уже закрыт или отменен при штатном отключении
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    public Task SendPingProbeAsync()
    {
        if (!Volatile.Read(ref _connected))
        {
            return Task.CompletedTask;
        }

        var buf = ArrayPool<byte>.Shared.Rent(11);
        buf[0] = 0xAA;
        buf[1] = 0xBB;
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(2, 8), Stopwatch.GetTimestamp());
        buf[10] = 0x11;
        SendPacketFromPool(buf, 11);
        return Task.CompletedTask;
    }

    public async Task<int> ProbeMtuAsync(int targetMtu, CancellationToken ct)
    {
        if (!Volatile.Read(ref _connected) || targetMtu < 64)
        {
            return -1;
        }

        var probeId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        long key = probeId;
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingMtuProbes[key] = tcs;

        var buf = ArrayPool<byte>.Shared.Rent(targetMtu);
        buf[0] = 0xAA;
        buf[1] = 0xBB;
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(2, 8), key);
        buf[10] = 0x11;
        RandomNumberGenerator.Fill(buf.AsSpan(11, targetMtu - 11));
        SendPacketFromPool(buf, targetMtu);

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromMilliseconds(1100));
            await using (linked.Token.Register(() => _ = tcs.TrySetResult(-1)))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            _ = _pendingMtuProbes.TryRemove(key, out _);
        }
    }

    public async Task<int> MeasureBurstMtuAsync(CancellationToken ct)
    {
        if (!Volatile.Read(ref _connected))
        {
            return -1;
        }

        lock (_receivedBurstMtus)
        {
            _receivedBurstMtus.Clear();
        }

        var marker = ArrayPool<byte>.Shared.Rent(2);
        marker[0] = 0xEE;
        marker[1] = 0x77;
        SendPacketFromPool(marker, 2);

        try
        {
            await Task.Delay(650, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            //ожидание прервано отменой измерения mtu
        }

        lock (_receivedBurstMtus)
        {
            return _receivedBurstMtus.Count > 0 ? _receivedBurstMtus.Max() : -1;
        }
    }

    public async Task SendMtuSyncAsync(int mtu)
    {
        if (!Volatile.Read(ref _connected))
        {
            return;
        }

        var ackTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingMtuSyncAcks[mtu] = ackTcs;

        var buf = ArrayPool<byte>.Shared.Rent(4);
        buf[0] = 0xCC;
        buf[1] = 0xDD;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), (ushort)mtu);

        if (!_txQueue.TryEnqueue(buf, 4))
        {
            ArrayPool<byte>.Shared.Return(buf);
            _ = _pendingMtuSyncAcks.TryRemove(mtu, out _);
            return;
        }

        try
        {
            _ = await ackTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(900)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            //сервер не успел подтвердить синхронизацию mtu за отведенное окно
        }
        finally
        {
            _ = _pendingMtuSyncAcks.TryRemove(mtu, out _);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _connected, false);
        _ = _warmSockets.Writer.TryComplete();
        DrainWarmSocketsSync();
        _txQueue.Dispose();
        _txQueue.DrainAndReturn(static b => ArrayPool<byte>.Shared.Return(b));
        _cts?.Cancel();
        _cts?.Dispose();
        Interlocked.Exchange(ref _activeLeg, null)?.Dispose();
        _handoffGate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _connected, false);
        _ = _warmSockets.Writer.TryComplete();

        using var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        await DrainWarmSocketsAsync().ConfigureAwait(false);

        var loops = _activeLoopTasks;
        _activeLoopTasks = [];
        if (loops.Length > 0)
        {
            try
            {
                await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(1.2), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or SocketException or ObjectDisposedException)
            {
                //фоновые циклы уже завершаются при остановке транспорта
            }
        }

        _txQueue.Dispose();
        _txQueue.DrainAndReturn(static b => ArrayPool<byte>.Shared.Return(b));

        if (Interlocked.Exchange(ref _activeLeg, null) is { } leg)
        {
            await leg.DisposeAsync().ConfigureAwait(false);
        }

        _handoffGate.Dispose();
    }

    private async Task<int> RunBulkEchoProbeAsync(int probeCount, int payloadSize, int timeoutMs, CancellationToken ct)
    {
        var tasks = new Task<int>[probeCount];
        long baseKey = RandomNumberGenerator.GetInt32(10_000, int.MaxValue - probeCount - 1);

        using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        windowCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        for (var i = 0; i < probeCount; i++)
        {
            var key = baseKey + i;
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingMtuProbes[key] = tcs;

            var buf = ArrayPool<byte>.Shared.Rent(payloadSize);
            buf[0] = 0xAA;
            buf[1] = 0xBB;
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(2, 8), key);
            buf[10] = 0x11;
            RandomNumberGenerator.Fill(buf.AsSpan(11, payloadSize - 11));
            SendPacketFromPool(buf, payloadSize);

            tasks[i] = WaitSingleProbeAsync(key, tcs, windowCts.Token);
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Count(static r => r > 0);

        async Task<int> WaitSingleProbeAsync(long probeKey, TaskCompletionSource<int> probeTcs, CancellationToken token)
        {
            try
            {
                await using (token.Register(() => _ = probeTcs.TrySetResult(-1)))
                {
                    return await probeTcs.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                _ = _pendingMtuProbes.TryRemove(probeKey, out _);
            }
        }
    }

    private void EnsureWarmStandbyProducerStarted(CancellationToken sessionCt)
    {
        if (_warmProducerTask is { IsCompleted: false })
        {
            return;
        }

        _warmProducerTask = Task.Run(() => WarmStandbyProducerLoopAsync(sessionCt), sessionCt);
    }

    //фоновый прогрев чистых tls 1.3 соединений для переключения карусели hydra за 0 мс rtt
    [SuppressMessage("Reliability", "CA2000:Ликвидировать объекты перед потерей области",
        Justification = "Владение потоком передается в очередь _warmSockets либо освобождается при переполнении очереди")]
    private async Task WarmStandbyProducerLoopAsync(CancellationToken ct)
    {
        var seq = 0;
        while (!ct.IsCancellationRequested && Volatile.Read(ref _disposed) == 0 && IsHydraCarouselActive)
        {
            try
            {
                if (!await _warmSockets.Writer.WaitToWriteAsync(ct).ConfigureAwait(false))
                {
                    break;
                }

                if (_targetIp is null)
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                    continue;
                }

                var idx = seq++;
                var port = _portCandidates[idx % _portCandidates.Length];
                var cover = _coverCandidates[idx % _coverCandidates.Length];

                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(TimeSpan.FromMilliseconds(3200));

                var warm = await OpenRawTlsSocketAsync(_targetIp, port, cover, connectCts.Token).ConfigureAwait(false);
                if (!_warmSockets.Writer.TryWrite(warm))
                {
                    await warm.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                try
                {
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    [SuppressMessage("Reliability", "CA2000:Ликвидировать объекты перед потерей области",
        Justification = "Владение сокетом передается в Tls13CryptoStream либо освобождается в блоке catch")]
    private async Task<WarmRealitySocket> OpenRawTlsSocketAsync(IPAddress targetIp, int port, string coverSni, CancellationToken ct)
    {
        Socket? socket = null;
        Tls13CryptoStream? tls = null;
        try
        {
            socket = new Socket(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
                SendBufferSize = 4194304,
                ReceiveBufferSize = 4194304
            };
            (_protectAction ?? OnSocketCreated ?? OctopusEngine.Current.ProtectSocket).Invoke(socket);

            await socket.ConnectAsync(new IPEndPoint(targetIp, port), ct).ConfigureAwait(false);

            var netStream = new NetworkStream(socket, true);
            Stream transportWire = IPAddress.IsLoopback(targetIp)
                ? netStream
                : new DpiBypassStream(netStream, delayMs: RandomNumberGenerator.GetInt32(6, 15), bypassEnabled: true)
                {
                    Mode = DpiBypassMode.TlsRecordMidsld
                };

            tls = await Tls13CryptoStream.ConnectClientAsync(
                    transportWire,
                    coverSni,
                    _realityPub,
                    _spkiPin,
                    leaveInnerOpen: false,
                    ct)
                .ConfigureAwait(false);

            return new WarmRealitySocket(tls, Stopwatch.GetTimestamp());
        }
        catch
        {
            if (tls is not null)
            {
                await tls.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                socket?.Dispose();
            }

            throw;
        }
    }

    private async Task<RealityLeg> PromoteWarmSocketToLegAsync(WarmRealitySocket warm, bool hydraActive, CancellationToken ct)
    {
        var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sessionSeed = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var sessionKey = ObxodkaFraming.DeriveSessionKey(_clientThumbprint, 0, sessionSeed);
        var clientBudget = hydraActive
            ? RandomNumberGenerator.GetInt32(HydraClientMinBudgetBytes, HydraClientMaxBudgetBytes)
            : 0;
        var serverBudget = hydraActive ? HydraServerBudgetBytes : 0;

        var hsStr = OctopusEngine.Current.BuildAuthenticatedHandshakeString(
            _clientThumbprint,
            _assignedIp,
            0,
            SmartMtuEngine.CurrentMtu,
            sessionSeed,
            nowSec,
            includePublicKey: true,
            hydraSocketBudgetBytes: serverBudget);

        var hsPayload = Encoding.UTF8.GetBytes(hsStr);
        var hsFrame = ObxodkaFraming.Pack(ObxodkaFraming.CmdHandshake, hsPayload, out var hsLen, SmartMtuEngine.CurrentMtu);
        var chaffFrame = ObxodkaFraming.Pack(ObxodkaFraming.CmdChaff, [], out var chaffLen, SmartMtuEngine.CurrentMtu, sessionKey);

        try
        {
            await warm.Stream.WriteAsync(hsFrame.AsMemory(0, hsLen), ct).ConfigureAwait(false);
            await warm.Stream.WriteAsync(chaffFrame.AsMemory(0, chaffLen), ct).ConfigureAwait(false);
            await warm.Stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(hsFrame);
            ArrayPool<byte>.Shared.Return(chaffFrame);
        }

        var leg = new RealityLeg(warm.Stream, sessionKey, clientBudget, hsLen + chaffLen);
        var sessionToken = _cts?.Token ?? ct;
        leg.ReaderTask = Task.Run(() => LegReaderLoopAsync(leg, sessionToken), sessionToken);
        return leg;
    }

    //бесшовное переключение активного звена карусели hydra с сохранением виртуального ip и очереди пакетов
    [SuppressMessage("Reliability", "CA2000:Ликвидировать объекты перед потерей области",
        Justification = "Владение новым звеном передается в _activeLeg, а устаревшие кандидаты освобождаются через DisposeAsync")]
    private async Task RotateActiveLegAsync(CancellationToken ct)
    {
        if (_targetIp is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await _handoffGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            WarmRealitySocket? warm = null;
            while (_warmSockets.Reader.TryRead(out var candidate))
            {
                var ageSec = (Stopwatch.GetTimestamp() - candidate.CreatedTicks) / (double)Stopwatch.Frequency;
                if (ageSec <= 25.0)
                {
                    warm = candidate;
                    break;
                }

                await candidate.DisposeAsync().ConfigureAwait(false);
            }

            if (warm is not { } selectedWarm)
            {
                var nextIdx = Interlocked.Increment(ref _hydraLegCounter);
                var port = _portCandidates[Math.Abs(nextIdx) % _portCandidates.Length];
                var cover = _coverCandidates[Math.Abs(nextIdx) % _coverCandidates.Length];
                using var freshCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                freshCts.CancelAfter(TimeSpan.FromMilliseconds(3200));
                selectedWarm = await OpenRawTlsSocketAsync(_targetIp, port, cover, freshCts.Token).ConfigureAwait(false);
            }
            else
            {
                _ = Interlocked.Increment(ref _hydraLegCounter);
            }

            var nextLeg = await PromoteWarmSocketToLegAsync(selectedWarm, hydraActive: true, ct).ConfigureAwait(false);
            using var hsWaitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hsWaitCts.CancelAfter(TimeSpan.FromMilliseconds(2200));
            _ = await nextLeg.HandshakeReady.Task.WaitAsync(hsWaitCts.Token).ConfigureAwait(false);

            var oldLeg = Interlocked.Exchange(ref _activeLeg, nextLeg);
            if (oldLeg is not null)
            {
                oldLeg.MarkRetired();
                _ = RetireLegGracefullyAsync(oldLeg);
            }
        }
        finally
        {
            _ = _handoffGate.Release();
        }
    }

    private static async Task RetireLegGracefullyAsync(RealityLeg oldLeg)
    {
        try
        {
            await Task.Delay(250).ConfigureAwait(false);
            await oldLeg.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            //старое плечо уже закрыто или сброшено удаленной стороной
        }
    }

    private static string[] BuildCoverSniList(string? configured) =>
        !string.IsNullOrWhiteSpace(configured) &&
        !IPAddress.TryParse(configured, out _) &&
        !SniEngine.IsInternalBrandHost(configured)
            ? [configured.Trim(), .. s_coverDonorHosts]
            : s_coverDonorHosts;

    private async Task CleanupFailedAttemptAsync(RealityLeg? failedLeg, bool ownershipTransferred)
    {
        Volatile.Write(ref _connected, false);
        using var attemptCts = Interlocked.Exchange(ref _cts, null);
        if (attemptCts is not null)
        {
            await attemptCts.CancelAsync().ConfigureAwait(false);
        }

        var loops = _activeLoopTasks;
        _activeLoopTasks = [];
        if (loops.Length > 0)
        {
            try
            {
                await Task.WhenAll(loops).WaitAsync(TimeSpan.FromMilliseconds(800), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or SocketException or ObjectDisposedException)
            {
                //фоновые циклы уже завершаются при остановке транспорта
            }
        }

        _txQueue.DrainAndReturn(static b => ArrayPool<byte>.Shared.Return(b));
        var legToClose = Interlocked.Exchange(ref _activeLeg, null) ?? (!ownershipTransferred ? failedLeg : null);
        if (legToClose is not null)
        {
            await legToClose.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task LegReaderLoopAsync(RealityLeg leg, CancellationToken ct)
    {
        var rxBuf = ArrayPool<byte>.Shared.Rent(ObxodkaFraming.MaxFrameSize * 2);
        var rxFilled = 0;
        var rxOffset = 0;

        try
        {
            while (!ct.IsCancellationRequested && !leg.IsDisposed)
            {
                if (rxFilled == rxBuf.Length)
                {
                    if (rxOffset > 0)
                    {
                        var rem = rxFilled - rxOffset;
                        rxBuf.AsSpan(rxOffset, rem).CopyTo(rxBuf);
                        rxFilled = rem;
                        rxOffset = 0;
                    }
                    else
                    {
                        var grown = ArrayPool<byte>.Shared.Rent(rxBuf.Length * 2);
                        rxBuf.AsSpan(0, rxFilled).CopyTo(grown);
                        ArrayPool<byte>.Shared.Return(rxBuf);
                        rxBuf = grown;
                    }
                }

                var got = await leg.Stream.ReadAsync(rxBuf.AsMemory(rxFilled, rxBuf.Length - rxFilled), ct).ConfigureAwait(false);
                if (got <= 0)
                {
                    break;
                }

                rxFilled += got;
                leg.AddRxBytes(got);

                while (rxFilled - rxOffset >= ObxodkaFraming.HeaderSize)
                {
                    var slice = rxBuf.AsSpan(rxOffset, rxFilled - rxOffset);
                    var activeKey = leg.SessionKey;
                    if (!ObxodkaFraming.TryDecodeHeader(slice, out var totalLen, out var payloadLen, out var cmd, out var salt, activeKey))
                    {
                        if (activeKey != 0 && ObxodkaFraming.TryDecodeHeader(slice, out totalLen, out payloadLen, out cmd, out salt, 0u))
                        {
                            activeKey = 0u;
                        }
                        else
                        {
                            return;
                        }
                    }

                    if (slice.Length < totalLen)
                    {
                        break;
                    }

                    var payloadSpan = slice.Slice(ObxodkaFraming.HeaderSize, payloadLen);
                    if (payloadLen > 0)
                    {
                        ObxodkaFraming.UnmaskPayload(payloadSpan, salt, activeKey);
                    }

                    if (cmd == ObxodkaFraming.CmdChaff && payloadLen == 0 && IsHydraCarouselActive && leg.HandshakeReady.Task.IsCompleted)
                    {
                        //сервер сообщил о достижении безопасного порога отдачи (<12 кб) на данном сокете
                        leg.SignalServerHandoff();
                    }
                    else
                    {
                        HandleIncomingFrame(leg, cmd, payloadSpan, payloadLen);
                    }

                    rxOffset += totalLen;
                }

                var remaining = rxFilled - rxOffset;
                if (remaining > 0)
                {
                    rxBuf.AsSpan(rxOffset, remaining).CopyTo(rxBuf);
                }

                rxFilled = remaining;
                rxOffset = 0;
            }
        }
        catch (OperationCanceledException)
        {
            //штатная остановка цикла чтения плеча
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or ObjectDisposedException)
        {
            if (!leg.IsRetired && ReferenceEquals(_activeLeg, leg))
            {
                AppLogger.LogWarning($"[OBXODKA-REALITY] Active leg reader stopped: {ex.Message}");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rxBuf);
            if (!leg.HandshakeReady.Task.IsCompleted)
            {
                _ = leg.HandshakeReady.TrySetCanceled(CancellationToken.None);
            }

            if (!_ipTcs.Task.IsCompleted && ReferenceEquals(_activeLeg, leg))
            {
                _ = _ipTcs.TrySetCanceled(ct.IsCancellationRequested ? ct : CancellationToken.None);
            }

            if (!leg.IsRetired && ReferenceEquals(_activeLeg, leg) && !IsHydraCarouselActive)
            {
                NotifyDropOnce();
            }
        }
    }

    private void HandleIncomingFrame(RealityLeg leg, byte command, ReadOnlySpan<byte> payloadSpan, int payloadLen)
    {
        switch (command)
        {
            case ObxodkaFraming.CmdHandshakeResponse:
                {
                    var info = Encoding.UTF8.GetString(payloadSpan);
                    if (!info.StartsWith("IP:", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    var parts = info[3..].Split('|');
                    var ip4 = parts[0];
                    var ip6 = parts.Length > 1 ? parts[1] : string.Empty;
                    foreach (var p in parts.Where(static p => p.StartsWith("MTU:", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (int.TryParse(p[4..], out var sMtu) &&
                            sMtu is >= SmartMtuEngine.MinMtu and <= SmartMtuEngine.MaxMtu)
                        {
                            OnMtuUpdated?.Invoke(sMtu);
                        }
                    }

                    _assignedIp = ip4;
                    _ = leg.HandshakeReady.TrySetResult(true);
                    _ = _ipTcs.TrySetResult((ip4, ip6));
                    return;
                }
            case ObxodkaFraming.CmdData when payloadLen > 0:
                {
                    if (OnPacketReceived is not { } onPacket)
                    {
                        return;
                    }

                    var pkt = ArrayPool<byte>.Shared.Rent(payloadLen);
                    payloadSpan.CopyTo(pkt);
                    onPacket.Invoke(pkt, payloadLen);
                    return;
                }
            case ObxodkaFraming.CmdPong:
                {
                    if (payloadLen >= 10 && payloadSpan[0] == 0xAA && payloadSpan[1] == 0xBB)
                    {
                        var key = BinaryPrimitives.ReadInt64LittleEndian(payloadSpan.Slice(2, 8));
                        if (_pendingMtuProbes.TryRemove(key, out var probeTcs))
                        {
                            _ = probeTcs.TrySetResult(payloadLen - 2);
                            return;
                        }
                    }

                    if (payloadLen >= 8)
                    {
                        var sentTicks = BinaryPrimitives.ReadInt64LittleEndian(payloadSpan[..8]);
                        var rttMs = (long)((Stopwatch.GetTimestamp() - sentTicks) * 1000.0 / Stopwatch.Frequency);
                        if (rttMs is >= 0 and < 30_000)
                        {
                            OnPingUpdated?.Invoke(rttMs);
                        }
                    }

                    return;
                }
            case ObxodkaFraming.CmdMtuSync when payloadLen >= 2:
                {
                    var ackMtu = BinaryPrimitives.ReadUInt16BigEndian(payloadSpan[..2]);
                    if (_pendingMtuSyncAcks.TryRemove(ackMtu, out var syncTcs))
                    {
                        _ = syncTcs.TrySetResult(true);
                    }

                    return;
                }
            case ObxodkaFraming.CmdMtuBurstPayload when payloadLen >= 4:
                {
                    var burstMtu = BinaryPrimitives.ReadUInt16BigEndian(payloadSpan.Slice(2, 2));
                    lock (_receivedBurstMtus)
                    {
                        _receivedBurstMtus.Add(burstMtu);
                    }

                    return;
                }
        }
    }

    private async Task WriterLoopAsync(CancellationToken ct)
    {
        const int coalesceCap = 131072;
        var coalesceBuf = ArrayPool<byte>.Shared.Rent(coalesceCap + ObxodkaFraming.MaxFrameSize);

        try
        {
            while (!ct.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
            {
                var (pkt, len) = await _txQueue.DequeueAsync(ct).ConfigureAwait(false);
                if (len <= 0)
                {
                    ArrayPool<byte>.Shared.Return(pkt);
                    continue;
                }

                if (IsHydraCarouselActive && _activeLeg is { } checkLeg && checkLeg.NeedsHydraRotation)
                {
                    try
                    {
                        await RotateActiveLegAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        AppLogger.LogWarning($"[OBX-HYDRA] Leg rotation warning: {ex.Message}");
                    }
                }

                var leg = _activeLeg;
                if (leg is null || leg.IsDisposed)
                {
                    ArrayPool<byte>.Shared.Return(pkt);
                    continue;
                }

                var coalescedLen = 0;
                var containsInnerHandshake = false;
                var maxBatchBytes = IsHydraCarouselActive
                    ? Math.Max(1600, leg.RemainingTxBudget)
                    : coalesceCap;

                PackSinglePacketIntoBuffer(pkt, len, leg, ref coalescedLen, ref containsInnerHandshake);

                var batchCount = 1;
                while (batchCount < 64 &&
                       coalescedLen < maxBatchBytes &&
                       (!IsHydraCarouselActive || coalescedLen + 1500 <= maxBatchBytes) &&
                       _txQueue.TryDequeue(out var next))
                {
                    if (next.length > 0)
                    {
                        PackSinglePacketIntoBuffer(next.buffer, next.length, leg, ref coalescedLen, ref containsInnerHandshake);
                        batchCount++;
                    }
                    else
                    {
                        ArrayPool<byte>.Shared.Return(next.buffer);
                    }
                }

                if (coalescedLen > 0)
                {
                    try
                    {
                        if (containsInnerHandshake && coalescedLen > 96)
                        {
                            var split = InnerTlsMorpher.ComputeIrregularRecordSplit(coalescedLen);
                            await leg.Stream.WriteAsync(coalesceBuf.AsMemory(0, split), ct).ConfigureAwait(false);
                            await leg.Stream.FlushAsync(ct).ConfigureAwait(false);
                            await leg.Stream.WriteAsync(coalesceBuf.AsMemory(split, coalescedLen - split), ct).ConfigureAwait(false);
                        }
                        else
                        {
                            await leg.Stream.WriteAsync(coalesceBuf.AsMemory(0, coalescedLen), ct).ConfigureAwait(false);
                        }

                        leg.AddTxBytes(coalescedLen);
                        if (_txQueue.Count == 0 || leg.NeedsHydraRotation)
                        {
                            await leg.Stream.FlushAsync(ct).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (IsHydraCarouselActive && !ct.IsCancellationRequested &&
                                               ex is IOException or SocketException or ObjectDisposedException)
                    {
                        await RotateActiveLegAsync(ct).ConfigureAwait(false);
                        if (_activeLeg is { } retryLeg)
                        {
                            await retryLeg.Stream.WriteAsync(coalesceBuf.AsMemory(0, coalescedLen), ct).ConfigureAwait(false);
                            await retryLeg.Stream.FlushAsync(ct).ConfigureAwait(false);
                            retryLeg.AddTxBytes(coalescedLen);
                        }
                    }
                }

                if (IsHydraCarouselActive && leg.NeedsHydraRotation)
                {
                    try
                    {
                        await RotateActiveLegAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        AppLogger.LogWarning($"[OBX-HYDRA] Post-write leg rotation warning: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            //штатная остановка цикла записи транспорта
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            AppLogger.LogWarning($"[OBXODKA-REALITY] Writer stopped: {ex.Message}");
            NotifyDropOnce();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(coalesceBuf);
        }

        void PackSinglePacketIntoBuffer(byte[] rawPkt, int rawLen, RealityLeg currentLeg, ref int offset, ref bool hasInnerHs)
        {
            try
            {
                var cmd = TransportFrameControlMapper.ClassifyOutgoingPacket(rawPkt, rawLen, out var pOff, out var pLen);
                var payload = pLen > 0 ? rawPkt.AsSpan(pOff, pLen) : ReadOnlySpan<byte>.Empty;

                if (cmd == ObxodkaFraming.CmdData && InnerTlsMorpher.IsInnerTlsControlOrHandshake(payload))
                {
                    hasInnerHs = true;
                }

                var early = !IsHydraCarouselActive && currentLeg.TxBytes + offset < ObxodkaFraming.EarlyStageWindowBytes;
                var pad = cmd == ObxodkaFraming.CmdPing && payload.Length >= 800
                    ? 0
                    : InnerTlsMorpher.CalculateAntiMlPadding(payload, cmd, SmartMtuEngine.CurrentMtu, early);
                var frameLen = ObxodkaFraming.HeaderSize + payload.Length + pad;
                ObxodkaFraming.WriteFrame(coalesceBuf.AsSpan(offset, frameLen), cmd, payload, pad, currentLeg.SessionKey);
                offset += frameLen;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rawPkt);
            }
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Volatile.Read(ref _connected))
        {
            try
            {
                await Task.Delay(RandomNumberGenerator.GetInt32(4100, 7600), ct).ConfigureAwait(false);
                await SendPingProbeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                break;
            }
        }
    }

    private void DrainWarmSocketsSync()
    {
        while (_warmSockets.Reader.TryRead(out var warm))
        {
            warm.Stream.Dispose();
        }
    }

    private async Task DrainWarmSocketsAsync()
    {
        while (_warmSockets.Reader.TryRead(out var warm))
        {
            await warm.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void NotifyDropOnce()
    {
        if (!Volatile.Read(ref _connected) || Interlocked.Exchange(ref _dropSignaled, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _connected, false);
        OnConnectionDropped?.Invoke();
    }

    private sealed record WarmRealitySocket(Tls13CryptoStream Stream, long CreatedTicks) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Stream.DisposeAsync();
    }

    private sealed class RealityLeg(Tls13CryptoStream stream, uint sessionKey, int clientTxBudget, long initialTxBytes)
        : IDisposable, IAsyncDisposable
    {
        private int _disposed;
        private int _retired;
        private long _rxBytes;
        private int _serverHandoff;
        private long _txBytes = initialTxBytes;

        public Tls13CryptoStream Stream { get; } = stream;
        public uint SessionKey { get; } = sessionKey;
        public TaskCompletionSource<bool> HandshakeReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ReaderTask { get; set; } = Task.CompletedTask;

        public long TxBytes => Volatile.Read(ref _txBytes);
        public int RemainingTxBudget => clientTxBudget <= 0 ? int.MaxValue : Math.Max(0, clientTxBudget - (int)Volatile.Read(ref _txBytes));
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public bool IsRetired => Volatile.Read(ref _retired) != 0;

        public bool NeedsHydraRotation =>
            Volatile.Read(ref _serverHandoff) != 0 ||
            clientTxBudget > 0 && (Volatile.Read(ref _txBytes) >= clientTxBudget || Volatile.Read(ref _rxBytes) >= HydraServerBudgetBytes);

        public void AddTxBytes(long delta) => _ = Interlocked.Add(ref _txBytes, delta);
        public void AddRxBytes(long delta) => _ = Interlocked.Add(ref _rxBytes, delta);
        public void SignalServerHandoff() => Volatile.Write(ref _serverHandoff, 1);
        public void MarkRetired() => Volatile.Write(ref _retired, 1);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Stream.Dispose();
            }
        }

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref _disposed, 1) == 0 ? Stream.DisposeAsync() : ValueTask.CompletedTask;
    }
}
