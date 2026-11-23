namespace obxodka.Core.Transports;

//дополнительный контракт транспорта для обратной связи о фактическом удаленном ip и его блокировке
public interface IHealthAwareTransport
{
    //фактический ip удаленного сокета текущего подключения
    public string? RemoteConnectedEndpointIp { get; }

    //пометка текущего удаленного ip как проблемного (0 rx / зависание тспу) для ротации в обход
    public void ReportUnhealthyEndpoint();
}
