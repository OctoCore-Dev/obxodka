namespace obxodka.Core.Pipeline;

//единый конвейер сквозной обработки сетевых пакетов (ssot pipeline)
//строгая последовательность: фильтр петель -> adblock -> split tunnel -> mss clamping -> smart mtu нарезка -> отправка в сокет
public sealed class PacketPipeline(OctopusEngine engine)
{
    private readonly OctopusEngine _engine = engine;

    //блокировать ли исходящий ipv6-трафик (режим только ipv4 для предотвращения любых ipv6-проверок)
    public bool BlockIpv6Traffic { get; set; }

    //делегат мгновенного локального отклонения заблокированного ipv6-пакета (например tcp rst)
    public Func<byte[], int, bool>? Ipv6BlockResponder { get; set; }

    //делегат перехвата платформенного раздельного туннелирования (split tunnel)
    public Func<byte[], int, bool>? SplitTunnelInterceptor { get; set; }

    //делегат инспекции входящего трафика (snoop dns для split tunnel)
    public Action<ReadOnlySpan<byte>, int>? IngressInspector { get; set; }

    //делегат блокировщика рекламы (dns sinkhole)
    public Func<byte[], int, byte[]?>? AdBlockFilter { get; set; }

    //делегат обратной доставки пакета в виртуальный сетевой интерфейс ос (wintun / android tun)
    public Action<byte[], int>? LocalDelivery { get; set; }

    //проверка нужно ли блокировать исходящий ipv6 пакет в режиме только ipv4
    public bool ShouldDropIpv6Packet(ReadOnlySpan<byte> packet) =>
        BlockIpv6Traffic && packet.Length > 0 && (packet[0] >> 4) == 6;

    //обработка исходящего l3-пакета из сетевого стека ос (uplink)
    public void ProcessOutgoingPacket(byte[] inputBuf, int length, IVpnTransport? transport)
    {
        if (length < 20 || transport is null || !_engine.IsConnected)
        {
            ArrayPool<byte>.Shared.Return(inputBuf);
            return;
        }

        var span = inputBuf.AsSpan(0, length);

        //этап 1: фильтрация петель трафика к vpn серверу
        if (_engine.IsServerDestination(span, length))
        {
            ArrayPool<byte>.Shared.Return(inputBuf);
            return;
        }

        //этап 1.5: опциональная блокировка исходящего ipv6-трафика (режим только ipv4)
        var version = inputBuf[0] >> 4;
        if (ShouldDropIpv6Packet(span))
        {
            _ = Ipv6BlockResponder?.Invoke(inputBuf, length);
            ArrayPool<byte>.Shared.Return(inputBuf);
            return;
        }

        //этап 2: dns sinkhole и блокировка рекламы
        var adBlock = AdBlockFilter;
        if (adBlock is not null)
        {
            var sinkholeResp = adBlock(inputBuf, length);
            if (sinkholeResp is not null)
            {
                LocalDelivery?.Invoke(sinkholeResp, sinkholeResp.Length);
                ArrayPool<byte>.Shared.Return(inputBuf);
                return;
            }
        }

        //этап 3: раздельное туннелирование для приложений и доменов
        var splitInterceptor = SplitTunnelInterceptor;
        if (splitInterceptor is not null && splitInterceptor(inputBuf, length))
        {
            ArrayPool<byte>.Shared.Return(inputBuf);
            return;
        }

        //этап 4: оптимизация tcp mss под текущий mtu туннеля
        var currentMtu = SmartMtuEngine.CurrentMtu;
        SmartMtuEngine.ClampTcpMss(span, currentMtu);

        //этап 5: смарт-нарезка пакетов превышающих mtu туннеля (без ложных icmp сбоев в сокеты игр)
        if (length > currentMtu)
        {
            if (version == 4)
            {
                //нарезаем на валидные ipv4 фрагменты с уникальным id и отправляем напрямую в сокет
                if (SmartMtuEngine.TryFragmentIpv4(span, length, currentMtu, transport.SendPacketFromPool))
                {
                    ArrayPool<byte>.Shared.Return(inputBuf);
                    return;
                }

                //если нарезать не удалось и стоит флаг df — уведомляем стек ос через icmp pmtud
                var isDf = (inputBuf[6] & 0x40) != 0;
                if (isDf && SmartMtuEngine.TryBuildIcmpFragmentationNeeded(span, length, currentMtu, out var icmpPkt, out var icmpLen))
                {
                    try
                    {
                        LocalDelivery?.Invoke(icmpPkt, icmpLen);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(icmpPkt);
                    }
                    ArrayPool<byte>.Shared.Return(inputBuf);
                    return;
                }
            }
            else if (version == 6 && SmartMtuEngine.TryBuildIcmpPacketTooBig(span, length, currentMtu, out var icmp6Pkt, out var icmp6Len))
            {
                try
                {
                    LocalDelivery?.Invoke(icmp6Pkt, icmp6Len);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(icmp6Pkt);
                }
                ArrayPool<byte>.Shared.Return(inputBuf);
                return;
            }
        }

        //этап 6: отправка пакета в транспортный канал
        transport.SendPacketFromPool(inputBuf, length);
    }

    //обработка входящего l3-пакета из туннеля vpn (downlink)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ProcessIncomingPacket(byte[] data, int length, Action<byte[], int>? deliverLocal)
    {
        try
        {
            if (length < 20)
            {
                return;
            }

            var span = data.AsSpan(0, length);

            //этап 1: аудит входящих ответов для раздельного туннелирования (dns snooper)
            IngressInspector?.Invoke(span, length);

            //этап 2: нормализация tcp mss для входящих сессий
            SmartMtuEngine.ClampTcpMss(span, SmartMtuEngine.CurrentMtu);

            //этап 3: инжекция пакета в виртуальный адаптер ос
            deliverLocal?.Invoke(data, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(data);
        }
    }
}
