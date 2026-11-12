namespace obxodka.Core;

public sealed partial class OctopusEngine
{
    private volatile uint[] _serverIpv4Array = [];
    private volatile byte[][] _serverIpv6Array = [];
    private readonly Lock _serverIpsLock = new();

    //регистрация ip адресов сервера для исключения из туннеля во избежание петель маршрутизации
    public void RegisterServerEndpoint(string serverIp)
    {
        if (string.IsNullOrWhiteSpace(serverIp))
        {
            return;
        }

        lock (_serverIpsLock)
        {
            try
            {
                var v4List = _serverIpv4Array.ToList();
                var v6List = _serverIpv6Array.ToList();
                var changed = false;

                void Add(IPAddress ip)
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

                if (IPAddress.TryParse(serverIp, out var directIp))
                {
                    Add(directIp);
                }
                else
                {
                    var addrs = Dns.GetHostAddresses(serverIp);
                    foreach (var addr in addrs)
                    {
                        Add(addr);
                    }
                }

                if (changed)
                {
                    _serverIpv4Array = [.. v4List];
                    _serverIpv6Array = [.. v6List];
                }
            }
            catch { }
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
        catch (Exception ex)
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
            PlatformServices.Preferences.Get("ForceWebSocket", false));

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
        ActiveRays = strategy.UseWebSocket ? 1 : 2;
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
            if (len > 600)
            {
                _ = Interlocked.Increment(ref _largeRxPackets);
            }
            SmartMtuEngine.ClampTcpMss(pkt.AsSpan(0, len), SmartMtuEngine.CurrentMtu);
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

    //бесшовная горячая замена прямого h2 транспорта на облачный websocket щит без пересоздания tun интерфейса
    [SuppressMessage("Reliability", "CA2000:Ликвидировать объекты перед потерей области", Justification = "Владение передается в _transport через Interlocked.Exchange либо освобождается через DisposeAsync в finally")]
    public async Task<bool> TryEscalateToShieldAsync(string reason, CancellationToken ct = default)
    {
        if (_transport is not ObxodkaStreamTransport || _clientCert is null || _cts is null || _cts.IsCancellationRequested)
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

        if (Interlocked.CompareExchange(ref _isEscalatingToShield, 1, 0) != 0)
        {
            return false;
        }

        ObxodkaWebSocketTransport? wsTransport = null;
        try
        {
            MarkNodeByteCapped(currentIp);
            DynamicSslPublicKeyHash = null;

            var shieldDomain = AppConfig.PrimaryDomain;
            AppLogger.LogWarning($"[OCTOPUS-SHIELD] TSPU freeze detected ({reason}) on {currentIp}:{ConnectedServerPort}. Escalating seamlessly to Shield '{shieldDomain}'...");

            wsTransport = new ObxodkaWebSocketTransport(443, shieldDomain, AssignedIp);
            if (_protectAction is not null)
            {
                wsTransport.ProtectSockets(_protectAction);
            }
            WireTransportEvents(wsTransport);

            using var escalateTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct, escalateTimeoutCts.Token);

            var (newIp, newIp6) = await wsTransport.ConnectAsync(shieldDomain, _clientCert.Thumbprint, linkedCts.Token).ConfigureAwait(false);

            var oldTransport = Interlocked.Exchange(ref _transport, wsTransport);
            wsTransport = null;

            ActiveProtocol = "OBXODKA-WS";
            ActiveRays = 1;
            ConnectedServerIp = shieldDomain;
            ConnectedServerPort = 443;
            ActiveTargetSni = shieldDomain;
            AssignedIp = newIp;
            AssignedIpV6 = newIp6;
            Volatile.Write(ref _watchdogArmedTicks, Environment.TickCount64);

            if (oldTransport is not null)
            {
                try
                {
                    await oldTransport.DisposeAsync().ConfigureAwait(false);
                }
                catch { }
            }

            AppLogger.Log($"[OCTOPUS-SHIELD] Seamless escalation complete! Tunnel preserved on {AssignedIp} via WebSocket Shield.");
            ScheduleSmartMtuScan(1500);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[OCTOPUS-SHIELD] Seamless escalation failed ({reason}): {ex.Message}");
            return false;
        }
        finally
        {
            if (wsTransport is not null)
            {
                try
                {
                    await wsTransport.DisposeAsync().ConfigureAwait(false);
                }
                catch { }
            }
            Volatile.Write(ref _isEscalatingToShield, 0);
        }
    }

    //реконнект с использованием сохраненных параметров активного соединения (ssot)
    public async Task ReconnectAsync()
    {
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
    public void ProtectTransportSockets(Action<Socket> protectAction)
    {
        _protectAction = protectAction;
        _transport?.ProtectSockets(protectAction);
    }

    //асинхронное освобождение ресурсов движка
    public async ValueTask DisposeAsync()
    {
        Volatile.Write(ref _watchdogArmedTicks, 0);
        _smoothedPing = 0;
        try
        {
            if (_mtuScanCts is not null)
            {
                await _mtuScanCts.CancelAsync().ConfigureAwait(false);
                _mtuScanCts.Dispose();
            }
        }
        catch { }
        _mtuScanCts = null;

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
        }
        _cts = null;

        _clientCert?.Dispose();
        _clientCert = null;

        if (_transport is not null)
        {
            await _transport.DisposeAsync();
            _transport = null;
        }

        GC.SuppressFinalize(this);
    }

    //синхронное освобождение ресурсов движка
    public void Dispose()
    {
        Volatile.Write(ref _watchdogArmedTicks, 0);
        try
        {
            _mtuScanCts?.Cancel();
            _mtuScanCts?.Dispose();
        }
        catch { }
        _mtuScanCts = null;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _clientCert?.Dispose();
        _clientCert = null;

        _transport?.Dispose();
        _transport = null;

        GC.SuppressFinalize(this);
    }
}
