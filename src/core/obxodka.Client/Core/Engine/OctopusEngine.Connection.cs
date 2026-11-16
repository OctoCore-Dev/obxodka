namespace obxodka.Core;

public sealed partial class OctopusEngine
{
    private volatile uint[] _serverIpv4Array = [];
    private volatile byte[][] _serverIpv6Array = [];
    private readonly Lock _serverIpsLock = new();

    //добавление уже известных ip адресов сервера в таблицу исключений без блокирующего dns
    private void RegisterServerIpAddresses(IEnumerable<IPAddress> addresses)
    {
        lock (_serverIpsLock)
        {
            var v4List = _serverIpv4Array.ToList();
            var v6List = _serverIpv6Array.ToList();
            var changed = false;

            foreach (var ip in addresses)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    var u = BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
                    if (!v4List.Contains(u))
                    {
                        v4List.Add(u);
                        changed = true;
                    }
                }
                else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    var b = ip.GetAddressBytes();
                    if (!v6List.Any(existing => existing.AsSpan().SequenceEqual(b)))
                    {
                        v6List.Add(b);
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                _serverIpv4Array = [.. v4List];
                _serverIpv6Array = [.. v6List];
            }
        }
    }

    //регистрация ip адресов сервера для исключения из туннеля во избежание петель маршрутизации
    public void RegisterServerEndpoint(string serverIp)
    {
        if (string.IsNullOrWhiteSpace(serverIp))
        {
            return;
        }

        try
        {
            if (IPAddress.TryParse(serverIp, out var directIp))
            {
                RegisterServerIpAddresses([directIp]);
            }
            else
            {
                var addrs = Dns.GetHostAddresses(serverIp);
                RegisterServerIpAddresses(addrs);
            }
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or NotSupportedException)
        {
            //подавление ошибки обновления адресов сервера
        }
    }

    //проверка направлен ли пакет на собственный ip сервера
    public bool IsServerDestination(ReadOnlySpan<byte> packet, int length)
    {
        if (length < 20)
        {
            return false;
        }

        var version = packet[0] >> 4;
        if (version == 4)
        {
            var dst = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(16, 4));
            var v4 = _serverIpv4Array;
            for (var i = 0; i < v4.Length; i++)
            {
                if (v4[i] == dst)
                {
                    return true;
                }
            }
        }
        else if (version == 6 && length >= 40)
        {
            var dstSpan = packet.Slice(24, 16);
            var v6 = _serverIpv6Array;
            for (var i = 0; i < v6.Length; i++)
            {
                if (dstSpan.SequenceEqual(v6[i]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    //подключение к vpn серверу по круговому конвейеру протоколов и серверов с обязательной проверкой rx > 0
    [SuppressMessage("Reliability", "CA2000:Ликвидировать объекты перед потерей области", Justification = "Владение транспортом хранится в _transport и освобождается при замене или в DisposeAsync")]
    public async Task<ProtocolRouteStep> ConnectRotationRingAsync(
        IReadOnlyList<ProtocolRouteStep> ring,
        Func<ProtocolRouteStep, CancellationToken, Task>? prepareTunnelBeforeVerifyAsync = null,
        CancellationToken ct = default)
    {
        if (ring is null || ring.Count == 0)
        {
            throw new ArgumentException("Список протоколов и серверов пуст.", nameof(ring));
        }

        if (!IsUplinkAvailable)
        {
            throw new PhysicalNetworkUnavailableException("Отсутствует подключение к сети (Wi-Fi или сетевой кабель отключены).");
        }

        await EnsureClientCertLoadedAsync().ConfigureAwait(false);
        _rotationRing = [.. ring];

        Exception? lastException = null;
        for (var i = 0; i < _rotationRing.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsUplinkAvailable)
            {
                throw new PhysicalNetworkUnavailableException("Подключение к физической сети потеряно.");
            }

            var step = _rotationRing[i];
            ConnectedServerIp = step.Host;
            ConnectedServerPort = step.Port;
            ActiveTargetSni = step.TargetSni;
            DynamicSslPublicKeyHash = step.ClearDynamicPinning ? null : DirectOriginCertHash;

            await PrepareExternalEndpointBypassAsync(step, ct).ConfigureAwait(false);

            SmartMtuEngine.CurrentMtu = step.Kind == TransportProtocolKind.ObxodkaStream
                ? SmartMtuEngine.InitialMtu
                : SmartMtuEngine.DefaultMtu;
            ResetTrafficCounters();

            var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var oldCts = Interlocked.Exchange(ref _cts, sessionCts);
            if (oldCts is not null)
            {
                try
                {
                    await oldCts.CancelAsync().ConfigureAwait(false);
                    oldCts.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            var stepToken = sessionCts.Token;
            var stepProtocol = FormatStepProtocolName(step);
            IVpnTransport? transport = null;
            try
            {
                transport = ProtocolPipelineRegistry.CreateTransport(step);
                if (_protectAction is not null)
                {
                    transport.ProtectSockets(_protectAction);
                }

                _transport = transport;
                ActiveProtocol = transport.ProtocolName;
                ActiveProtocolDisplayName = stepProtocol;
                ActiveRays = step.Kind == TransportProtocolKind.ObxodkaStream ? 2 : 1;
                OnProtocolChanged?.Invoke();
                WireTransportEvents(transport);

                EmitStatus(VpnStatusMessages.TryingProtocol(stepProtocol, i + 1, _rotationRing.Count));
                AppLogger.Log($"[OCTOPUS-RING] Step {i + 1}/{_rotationRing.Count}: {transport.ProtocolName} -> {step.Host}:{step.Port} (SNI={step.TargetSni ?? "none"})");

                var (ip, ip6) = await transport.ConnectAsync(step.Host, _clientCert?.Thumbprint ?? "", stepToken).ConfigureAwait(false);
                AssignedIp = ip;
                AssignedIpV6 = ip6;

                if (prepareTunnelBeforeVerifyAsync is not null)
                {
                    await prepareTunnelBeforeVerifyAsync(step, stepToken).ConfigureAwait(false);
                }

                //жесткая проверка двустороннего трафика: без реального rx/cmdpong шаг считается мертвым
                var verified = await VerifyDownlinkAsync(TimeSpan.FromMilliseconds(3500), forceFreshProbe: true, ct: stepToken).ConfigureAwait(false);
                if (!verified)
                {
                    AppLogger.LogWarning($"[OCTOPUS-RING] Step {i + 1}/{_rotationRing.Count} ({transport.ProtocolName} @ {step.Host}) returned 0 RX. Rotating...");
                    if (transport is IHealthAwareTransport healthAware)
                    {
                        healthAware.ReportUnhealthyEndpoint();
                    }

                    if (!step.IsShield && step.Kind == TransportProtocolKind.ObxodkaStream)
                    {
                        MarkNodeByteCapped(step.Host);
                    }

                    lastException = new TimeoutException($"Протокол {transport.ProtocolName} ({step.Host}:{step.Port}) не получил ответных пакетов (0 RX).");
                    if (i + 1 < _rotationRing.Count)
                    {
                        EmitStatus(VpnStatusMessages.ProtocolUnavailableSwitching(stepProtocol, FormatStepProtocolName(_rotationRing[i + 1])));
                    }

                    await DisposeAttemptTransportAsync(transport).ConfigureAwait(false);
                    continue;
                }

                _rotationStepIndex = i;
                Volatile.Write(ref _lastHealthyRxTicks, Stopwatch.GetTimestamp());
                AppLogger.Log($"[OCTOPUS-RING] Connected & verified (RX OK): {transport.ProtocolName} @ {step.Host}:{step.Port} -> IPv4={ip}, IPv6={ip6}");
                StartTrafficMonitor();
                ArmTrafficWatchdog();
                return step;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await DisposeAttemptTransportAsync(transport).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not UnauthorizedAccessException)
            {
                lastException = ex;
                AppLogger.LogWarning($"[OCTOPUS-RING] Step {i + 1}/{_rotationRing.Count} ({step.Kind} @ {step.Host}:{step.Port}) failed: {ex.Message}");
                if (transport is IHealthAwareTransport healthAware)
                {
                    healthAware.ReportUnhealthyEndpoint();
                }

                if (i + 1 < _rotationRing.Count)
                {
                    EmitStatus(VpnStatusMessages.ProtocolUnavailableSwitching(stepProtocol, FormatStepProtocolName(_rotationRing[i + 1])));
                }

                await DisposeAttemptTransportAsync(transport).ConfigureAwait(false);
            }
        }

        await DisposeAsync().ConfigureAwait(false);
        throw lastException ?? new InvalidOperationException("Не удалось установить двустороннее соединение (0 RX) ни по одному из протоколов.");
    }

    //загрузка и валидация клиентского сертификата из сохранённой сессии
    private async Task EnsureClientCertLoadedAsync()
    {
        var session = await AuthManager.LoadSessionAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(session.VpnConfig))
        {
            throw new InvalidOperationException("Сертификат VPN отсутствует. Авторизуйтесь заново.");
        }

        var certBytes = Convert.FromBase64String(session.VpnConfig);
        try
        {
            var loadedCert = X509CertificateLoader.LoadPkcs12(certBytes, AppSecrets.InternalPfxPassword, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
            Interlocked.Exchange(ref _clientCert, loadedCert)?.Dispose();
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or PlatformNotSupportedException or ArgumentException)
        {
            AppLogger.LogWarning($"[VPN] Обнаружен устаревший сертификат: {ex.Message}. Требуется повторная авторизация.");
            OnCertificateRevoked?.Invoke("Обнаружен устаревший сертификат. Пожалуйста, войдите снова.");
            throw new UnauthorizedAccessException("Old certificate", ex);
        }
    }

    //регистрация внешних ip адреса назначения для bypass-маршрута ос перед открытием сокета без синхронной блокировки dns
    private async Task PrepareExternalEndpointBypassAsync(ProtocolRouteStep step, CancellationToken ct)
    {
        HashSet<string> ips = [with(StringComparer.OrdinalIgnoreCase)];

        if (IPAddress.TryParse(step.Host, out var literalIp))
        {
            RegisterServerIpAddresses([literalIp]);
            _ = ips.Add(step.Host);
        }
        else
        {
            try
            {
                using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                dnsCts.CancelAfter(TimeSpan.FromMilliseconds(1800));
                var resolved = await ApiService.ResolveAllHostsAntiPoisonAsync(step.Host, dnsCts.Token).ConfigureAwait(false);
                RegisterServerIpAddresses(resolved);
                foreach (var addr in resolved)
                {
                    if (addr.AddressFamily == AddressFamily.InterNetwork)
                    {
                        _ = ips.Add(addr.ToString());
                    }
                }
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                AppLogger.LogWarning($"[OCTOPUS-DNS] DNS resolution failed for {step.Host}: {ex.Message}");
            }
        }

        foreach (var relayIp in AppConfig.DirectRelayIps)
        {
            if (!string.IsNullOrWhiteSpace(relayIp) && IPAddress.TryParse(relayIp, out var relayParsed))
            {
                _ = ips.Add(relayIp);
                RegisterServerIpAddresses([relayParsed]);
            }
        }

        var hook = EnsureExternalHostRoutesAsync;
        if (hook is not null && ips.Count > 0)
        {
            await hook([.. ips], ct).ConfigureAwait(false);
        }
    }

    //безопасная очистка неудачного транспорта конкретной попытки кольца
    private async Task DisposeAttemptTransportAsync(IVpnTransport? transport)
    {
        if (transport is null)
        {
            return;
        }

        _ = Interlocked.CompareExchange(ref _transport, null, transport);
        try
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or TimeoutException or IOException or SocketException)
        {
            //подавление ошибки закрытия неудачного транспорта
        }
    }

    //подключение к vpn серверу и инициализация транспорта
    public async Task ConnectAsync(string serverIp, int serverPort, string? targetSni = null)
    {
        if (IsConnected)
        {
            return;
        }

        //проверка физической доступности сети перед попыткой подключения
        if (!IsUplinkAvailable)
        {
            throw new PhysicalNetworkUnavailableException("Отсутствует подключение к сети (Wi-Fi или сетевой кабель отключены).");
        }

        //автоматический уход на облачный щит если прямой ip ранее завис на пороге байт тспу
        if (IPAddress.TryParse(serverIp, out var directCandidateIp) &&
            !IPAddress.IsLoopback(directCandidateIp) &&
            IsNodeByteCapped(serverIp))
        {
            AppLogger.LogWarning($"[OCTOPUS-SHIELD] Direct node {serverIp} is penalized by TSPU byte-cap detector. Routing via Shield '{AppConfig.PrimaryDomain}'.");
            serverIp = AppConfig.PrimaryDomain;
            serverPort = 443;
            targetSni = AppConfig.PrimaryDomain;
        }

        ConnectedServerIp = serverIp;
        ConnectedServerPort = serverPort;

        SmartMtuEngine.CurrentMtu = SmartMtuEngine.InitialMtu;
        RegisterServerEndpoint(serverIp);
        foreach (var relayIp in AppConfig.DirectRelayIps)
        {
            RegisterServerEndpoint(relayIp);
        }

        var session = await AuthManager.LoadSessionAsync();
        if (string.IsNullOrEmpty(session.VpnConfig))
        {
            throw new InvalidOperationException("Сертификат VPN отсутствует. Авторизуйтесь заново.");
        }

        var certBytes = Convert.FromBase64String(session.VpnConfig);
        try
        {
            _clientCert = X509CertificateLoader.LoadPkcs12(certBytes, AppSecrets.InternalPfxPassword, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or PlatformNotSupportedException or ArgumentException)
        {
            AppLogger.LogWarning($"[VPN] Обнаружен устаревший сертификат: {ex.Message}. Требуется повторная авторизация.");
            OnCertificateRevoked?.Invoke("Обнаружен устаревший сертификат. Пожалуйста, войдите снова.");
            throw new UnauthorizedAccessException("Old certificate", ex);
        }

        _cts = new CancellationTokenSource();
        ActiveRays = 2;

        if (!string.IsNullOrWhiteSpace(targetSni))
        {
            ActiveTargetSni = targetSni;
        }
        else if (!string.IsNullOrWhiteSpace(ActiveTargetSni))
        {
            targetSni = ActiveTargetSni;
        }

        var strategy = SniEngine.ResolveTransportStrategy(
            serverIp,
            targetSni,
            PlatformServices.Preferences.Get(AppPreferenceKeys.ForceWebSocket, false));

        if (strategy.ClearDynamicPinning)
        {
            DynamicSslPublicKeyHash = null;
        }

        AppLogger.Log($"[OCTOPUS] Initializing transport (mode: {(strategy.UseWebSocket ? "WebSocket" : "H2Stream")}) to {serverIp}:{serverPort} with SNI '{strategy.EffectiveSni ?? "none"}'");
        IVpnTransport transport = strategy.UseWebSocket
            ? new ObxodkaWebSocketTransport(serverPort, strategy.EffectiveSni)
            : new ObxodkaStreamTransport(serverPort, strategy.EffectiveSni);
        if (_protectAction is not null)
        {
            transport.ProtectSockets(_protectAction);
        }
        _transport = transport;
        ActiveProtocol = transport.ProtocolName;
        ActiveProtocolDisplayName = VpnStatusMessages.FormatProtocolName(transport.ProtocolName, strategy.UseWebSocket);
        ActiveRays = strategy.UseWebSocket ? 1 : 2;
        OnProtocolChanged?.Invoke();
        WireTransportEvents(transport);

        var (ip, ip6) = await transport.ConnectAsync(serverIp, _clientCert?.Thumbprint ?? "", _cts.Token);
        AssignedIp = ip;
        AssignedIpV6 = ip6;
        AppLogger.Log($"[OCTOPUS] Handshake complete with {serverIp}:{serverPort}! Assigned IPv4: {ip}, IPv6: {ip6}");
        StartTrafficMonitor();
    }

    //привязка событий конкретного экземпляра транспорта с защитой от гонок при горячей замене
    private void WireTransportEvents(IVpnTransport transport)
    {
        transport.OnPacketReceived += (pkt, len) =>
        {
            _ = Interlocked.Add(ref _totalBytesReceived, len);
            Volatile.Write(ref _lastHealthyRxTicks, Stopwatch.GetTimestamp());
            if (len > 600)
            {
                _ = Interlocked.Increment(ref _largeRxPackets);
            }
            OnProbePacketInspect?.Invoke(pkt, len);
            var handler = OnPacketReceived;
            if (handler is not null)
            {
                handler.Invoke(pkt, len);
            }
            else
            {
                ArrayPool<byte>.Shared.Return(pkt);
            }
        };
        transport.OnPingUpdated += ReportPing;
        transport.OnConnectionDropped += () =>
        {
            if (ReferenceEquals(_transport, transport) && Volatile.Read(ref _isEscalatingToShield) == 0)
            {
                OnConnectionDropped?.Invoke();
            }
        };
        transport.OnMtuUpdated += mtu =>
        {
            if (ReferenceEquals(_transport, transport))
            {
                OnMtuUpdated?.Invoke(mtu);
            }
        };
    }

    //обратная совместимость: переключение на следующий живой протокол по кругу при заморозке
    public Task<bool> TryEscalateToShieldAsync(string reason, CancellationToken ct = default) =>
        TryRotateProtocolAsync(reason, ct);

    //бесшовная круговая ротация протокола (obxodka stream -> websocket -> xhttp -> резервные узлы) с проверкой rx > 0
    [SuppressMessage("Reliability", "CA2000:Ликвидировать объекты перед потерей области", Justification = "Владение передается в _transport через Interlocked.Exchange либо освобождается через DisposeAsync в finally")]
    public async Task<bool> TryRotateProtocolAsync(string reason, CancellationToken ct = default)
    {
        if (_clientCert is null || _cts is null || _cts.IsCancellationRequested)
        {
            return false;
        }

        var currentIp = ConnectedServerIp;
        if (!string.IsNullOrWhiteSpace(currentIp) &&
            IPAddress.TryParse(currentIp, out var parsedCurIp) &&
            IPAddress.IsLoopback(parsedCurIp))
        {
            return false;
        }

        if (!await _rotationGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return false;
        }

        _ = Interlocked.Exchange(ref _isEscalatingToShield, 1);
        IVpnTransport? candidateTransport = null;
        try
        {
            if (_rotationRing.Count == 0)
            {
                _rotationRing = SniEngine.BuildProtocolRotationRing(
                    null,
                    ConnectedServerIp,
                    ConnectedServerPort > 0 ? ConnectedServerPort : 443,
                    ActiveTargetSni,
                    PlatformServices.Preferences.Get(AppPreferenceKeys.ForceWebSocket, false));
            }

            if (_rotationRing.Count == 0)
            {
                return false;
            }

            if (_transport is IHealthAwareTransport curHealthAware)
            {
                curHealthAware.ReportUnhealthyEndpoint();
            }

            if (_transport is ObxodkaStreamTransport && !string.IsNullOrWhiteSpace(currentIp))
            {
                MarkNodeByteCapped(currentIp);
            }

            var originalTransport = _transport;
            var totalSteps = _rotationRing.Count;
            string? failedCandidateName = null;

            for (var offset = 1; offset <= totalSteps; offset++)
            {
                if (_cts is null || _cts.IsCancellationRequested || ct.IsCancellationRequested)
                {
                    break;
                }

                var candidateIdx = (_rotationStepIndex + offset) % totalSteps;
                var nextStep = _rotationRing[candidateIdx];

                //если прямой узел не отвечает в префлайте или попал под лимит тспу, пропускаем прямые шаги и чередуем щиты
                if (!nextStep.IsShield &&
                    (nextStep.Kind is TransportProtocolKind.ObxodkaStream or TransportProtocolKind.ObxodkaXhttp) &&
                    IsNodeByteCapped(nextStep.Host))
                {
                    continue;
                }

                DynamicSslPublicKeyHash = nextStep.ClearDynamicPinning ? null : DirectOriginCertHash;
                await PrepareExternalEndpointBypassAsync(nextStep, ct).ConfigureAwait(false);

                var nextProtocolName = FormatStepProtocolName(nextStep);
                EmitStatus(failedCandidateName is null
                    ? VpnStatusMessages.RotatingProtocol(nextProtocolName)
                    : VpnStatusMessages.ProtocolUnavailableSwitching(failedCandidateName, nextProtocolName));
                AppLogger.LogWarning($"[OCTOPUS-ROTATE] {reason}: trying step {candidateIdx + 1}/{totalSteps} ({nextStep.Kind} -> {nextStep.Host}:{nextStep.Port})");

                candidateTransport = ProtocolPipelineRegistry.CreateTransport(nextStep, AssignedIp);
                if (_protectAction is not null)
                {
                    candidateTransport.ProtectSockets(_protectAction);
                }

                WireTransportEvents(candidateTransport);

                try
                {
                    using var rotateTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct, rotateTimeoutCts.Token);

                    var (newIp, newIp6) = await candidateTransport.ConnectAsync(nextStep.Host, _clientCert.Thumbprint, linkedCts.Token).ConfigureAwait(false);

                    //временно переключаем активный транспорт для честного живого эхо-зонда на новом канале
                    _ = Interlocked.Exchange(ref _transport, candidateTransport);
                    var verified = await VerifyDownlinkAsync(TimeSpan.FromMilliseconds(3500), forceFreshProbe: true, ct: linkedCts.Token).ConfigureAwait(false);
                    if (!verified)
                    {
                        AppLogger.LogWarning($"[OCTOPUS-ROTATE] Candidate {nextStep.Kind} @ {nextStep.Host} yielded 0 RX. Advancing ring...");
                        if (candidateTransport is IHealthAwareTransport badCandidate)
                        {
                            badCandidate.ReportUnhealthyEndpoint();
                        }

                        if (!nextStep.IsShield && nextStep.Kind == TransportProtocolKind.ObxodkaStream)
                        {
                            MarkNodeByteCapped(nextStep.Host);
                        }

                        failedCandidateName = nextProtocolName;
                        _ = Interlocked.CompareExchange(ref _transport, originalTransport, candidateTransport);
                        await candidateTransport.DisposeAsync().ConfigureAwait(false);
                        candidateTransport = null;
                        continue;
                    }

                    //новый канал подтвердил реальный rx/cmdpong: закрепляем переключение
                    var swappedTransport = candidateTransport;
                    candidateTransport = null;

                    var prevIp = AssignedIp;
                    _rotationStepIndex = candidateIdx;
                    ActiveProtocol = swappedTransport.ProtocolName;
                    ActiveProtocolDisplayName = nextProtocolName;
                    ActiveRays = nextStep.Kind == TransportProtocolKind.ObxodkaStream ? 2 : 1;
                    ConnectedServerIp = nextStep.Host;
                    ConnectedServerPort = nextStep.Port;
                    ActiveTargetSni = nextStep.TargetSni;
                    AssignedIp = newIp;
                    AssignedIpV6 = newIp6;
                    Volatile.Write(ref _lastHealthyRxTicks, Stopwatch.GetTimestamp());
                    Volatile.Write(ref _watchdogArmedTicks, Environment.TickCount64);
                    OnProtocolChanged?.Invoke();

                    if (!string.Equals(prevIp, newIp, StringComparison.OrdinalIgnoreCase))
                    {
                        OnTunnelIpChanged?.Invoke();
                    }

                    if (originalTransport is not null && !ReferenceEquals(originalTransport, swappedTransport))
                    {
                        try
                        {
                            await originalTransport.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or TimeoutException or IOException or SocketException)
                        {
                            //подавление ошибки закрытия предыдущего транспорта
                        }
                    }

                    EmitStatus(VpnStatusMessages.ProtocolSwitched(nextProtocolName));
                    AppLogger.Log($"[OCTOPUS-ROTATE] Seamless switch succeeded! Active={ActiveProtocol} @ {ConnectedServerIp}:{ConnectedServerPort} (TUN={AssignedIp})");
                    if (swappedTransport is ObxodkaStreamTransport)
                    {
                        ScheduleSmartMtuScan(1500);
                    }
                    else
                    {
                        LockEdgeTransportMtu(swappedTransport);
                    }

                    return true;
                }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or SocketException or WebSocketException or HttpRequestException or InvalidOperationException or CryptographicException or ObjectDisposedException)
                {
                    failedCandidateName = nextProtocolName;
                    AppLogger.LogWarning($"[OCTOPUS-ROTATE] Step {nextStep.Kind} @ {nextStep.Host} failed: {ex.Message}");
                    if (candidateTransport is IHealthAwareTransport badCandidate)
                    {
                        badCandidate.ReportUnhealthyEndpoint();
                    }

                    if (_transport == candidateTransport)
                    {
                        _ = Interlocked.CompareExchange(ref _transport, originalTransport, candidateTransport);
                    }

                    if (candidateTransport is not null)
                    {
                        try
                        {
                            await candidateTransport.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (Exception disposeEx) when (disposeEx is OperationCanceledException or ObjectDisposedException or TimeoutException or IOException or SocketException)
                        {
                        }
                        candidateTransport = null;
                    }
                }
            }

            return false;
        }
        finally
        {
            if (candidateTransport is not null)
            {
                try
                {
                    await candidateTransport.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or TimeoutException or IOException or SocketException)
                {
                }
            }
            Volatile.Write(ref _isEscalatingToShield, 0);
            _ = _rotationGate.Release();
        }
    }

    //реконнект с использованием сохраненных параметров или кругового конвейера протоколов (ssot)
    public async Task ReconnectAsync()
    {
        if (_rotationRing.Count > 0)
        {
            var rotatedRing = new List<ProtocolRouteStep>(_rotationRing.Count);
            var startIdx = (_rotationStepIndex + 1) % _rotationRing.Count;
            for (var i = 0; i < _rotationRing.Count; i++)
            {
                rotatedRing.Add(_rotationRing[(startIdx + i) % _rotationRing.Count]);
            }

            await DisposeAsync().ConfigureAwait(false);
            _ = await ConnectRotationRingAsync(rotatedRing).ConfigureAwait(false);
            return;
        }

        var ip = ConnectedServerIp;
        var port = ConnectedServerPort;
        var sni = ActiveTargetSni;
        if (string.IsNullOrEmpty(ip))
        {
            throw new InvalidOperationException("Невозможно переподключиться без предварительно установленного соединения.");
        }

        await ReconnectAsync(ip, port, sni);
    }

    //переподключение к vpn серверу с новыми параметрами
    public async Task ReconnectAsync(string serverIp, int serverPort, string? targetSni = null)
    {
        ConnectedServerIp = serverIp;
        ConnectedServerPort = serverPort;
        await DisposeAsync();
        await ConnectAsync(serverIp, serverPort, targetSni ?? ActiveTargetSni);
    }

    //установка платформенного обработчика защиты сокетов
    public void ProtectTransportSockets(Action<Socket>? protectAction)
    {
        _protectAction = protectAction;
        if (protectAction is not null)
        {
            _transport?.ProtectSockets(ProtectSocket);
        }
    }

    //единая централизованная защита сокетов всех транспортов и зондов через активный платформенный адаптер
    public void ProtectSocket(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        _protectAction?.Invoke(socket);
    }

    //асинхронное освобождение ресурсов движка
    public async ValueTask DisposeAsync()
    {
        Volatile.Write(ref _watchdogArmedTicks, 0);
        _smoothedPing = 0;
        var scanCts = Interlocked.Exchange(ref _mtuScanCts, null);
        if (scanCts is not null)
        {
            try
            {
                using (scanCts)
                {
                    await scanCts.CancelAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or AggregateException)
            {
                //подавление ошибки отмены сканирования mtu при остановке
            }
        }

        var engineCts = Interlocked.Exchange(ref _cts, null);
        if (engineCts is not null)
        {
            using (engineCts)
            {
                await engineCts.CancelAsync().ConfigureAwait(false);
            }
        }

        Interlocked.Exchange(ref _clientCert, null)?.Dispose();

        var transport = Interlocked.Exchange(ref _transport, null);
        if (transport is not null)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    //получение понятного названия протокола для шага кругового конвейера
    private static string FormatStepProtocolName(ProtocolRouteStep step)
    {
        var rawName = ProtocolPipelineRegistry.TryGetFactory(step.Kind, out var factory)
            ? factory.DisplayName
            : step.Kind.ToString();
        return VpnStatusMessages.FormatProtocolName(rawName, step.IsShield);
    }

    //синхронное освобождение ресурсов движка
    public void Dispose()
    {
        Volatile.Write(ref _watchdogArmedTicks, 0);
        var scanCts = Interlocked.Exchange(ref _mtuScanCts, null);
        if (scanCts is not null)
        {
            try
            {
                scanCts.Cancel();
                scanCts.Dispose();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or AggregateException)
            {
                //подавление ошибки отмены токена сканирования mtu
            }
        }

        var engineCts = Interlocked.Exchange(ref _cts, null);
        if (engineCts is not null)
        {
            engineCts.Cancel();
            engineCts.Dispose();
        }

        Interlocked.Exchange(ref _clientCert, null)?.Dispose();

        Interlocked.Exchange(ref _transport, null)?.Dispose();

        GC.SuppressFinalize(this);
    }
}
