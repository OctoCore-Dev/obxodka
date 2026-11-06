namespace obxodka.Platforms.Windows;

//обработка входящего и исходящего сетевого трафика через адаптер wintun
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed partial class WindowsVpnService
{
    //цикл обработки входящего и исходящего трафика wintun
    private async Task ProcessTrafficAsync(CancellationToken ct)
    {
        OctopusEngine.Current.OnPacketReceived -= HandlePacketFromVpn;
        OctopusEngine.Current.OnPacketReceived += HandlePacketFromVpn;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var txThread = new Thread(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            Thread.CurrentThread.Name = "Wintun-UploadReader";
            var batch = new PacketBatch();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var adapter = _adapter;
                    if (adapter is null)
                    {
                        break;
                    }

                    var count = adapter.ReceiveBatch(batch, ct);
                    var processed = 0;
                    try
                    {
                        for (var i = 0; i < count; i++)
                        {
                            processed = i;
                            var (buf, len) = batch[i];
                            if (OctopusEngine.Current.IsServerDestination(buf.AsSpan(0, len), len))
                            {
                                ArrayPool<byte>.Shared.Return(buf);
                                continue;
                            }

                            var sinkholeResp = DnsAdBlocker.ProcessPacket(buf, len);
                            if (sinkholeResp is not null)
                            {
                                adapter.SendPacket(sinkholeResp);
                                ArrayPool<byte>.Shared.Return(buf);
                                continue;
                            }

                            if (WindowsSplitTunnelEngine.TryInterceptOutgoingPacket(buf, len, adapter))
                            {
                                ArrayPool<byte>.Shared.Return(buf);
                                continue;
                            }

                            OctopusEngine.Current.SendPacketFromPool(buf, len);
                        }
                        processed = count;
                    }
                    finally
                    {
                        for (var i = processed; i < count; i++)
                        {
                            var (buf, _) = batch[i];
                            if (buf != null)
                            {
                                ArrayPool<byte>.Shared.Return(buf);
                            }
                        }
                    }
                }
            }
            catch { }
            finally
            {
                _ = tcs.TrySetResult();
            }
        })
        {
            IsBackground = true
        };
        txThread.Start();

        try
        {
            await tcs.Task;
        }
        finally
        {
            OctopusEngine.Current.OnPacketReceived -= HandlePacketFromVpn;
        }
    }

    //обработка пакета полученного из туннеля vpn
    private void HandlePacketFromVpn(byte[] data, int length)
    {
        try
        {
            WindowsSplitTunnelEngine.InspectIncomingPacketFromVpn(data.AsSpan(0, length), length);
            _adapter?.SendPacket(data, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(data);
        }
    }
}
