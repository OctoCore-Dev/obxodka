namespace obxodka.Platforms.Windows;

//обработка входящего и исходящего сетевого трафика через адаптер wintun
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed partial class WindowsVpnService
{
    //цикл обработки входящего и исходящего трафика wintun
    private async Task ProcessTrafficAsync(CancellationToken ct)
    {
        //конфигурация сквозного конвейера обработки пакетов
        var pipeline = OctopusEngine.Current.Pipeline;
        pipeline.SplitTunnelInterceptor = (buf, len) => _adapter is { } ad && WindowsSplitTunnelEngine.TryInterceptOutgoingPacket(buf, len, ad);
        pipeline.IngressInspector = (span, len) => WindowsSplitTunnelEngine.InspectIncomingPacketFromVpn(span, len);
        pipeline.AdBlockFilter = (buf, len) => DnsAdBlocker.ProcessPacket(buf, len);
        pipeline.LocalDelivery = (buf, len) => _adapter?.SendPacket(buf, len);

        OctopusEngine.Current.OnPacketReceived -= HandlePacketFromVpn;
        OctopusEngine.Current.OnPacketReceived += HandlePacketFromVpn;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var txThread = new Thread(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
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

    //обработка пакета полученного из туннеля vpn через сквозной конвейер
    private void HandlePacketFromVpn(byte[] data, int length) =>
        OctopusEngine.Current.Pipeline.ProcessIncomingPacket(data, length, (buf, len) => _adapter?.SendPacket(buf, len));
}
