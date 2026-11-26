namespace obxodka.Core.Transports;

//дополнительный контракт транспорта для обратной связи о фактическом удаленном ip и его блокировке
public interface IHealthAwareTransport
{
    //фактический ip удаленного сокета текущего подключения
    public string? RemoteConnectedEndpointIp { get; }

    //пометка текущего удаленного ip как проблемного (0 rx / зависание тспу) для ротации в обход
    public void ReportUnhealthyEndpoint();
}

//контракт транспорта со встроенной защитой от пороговой заморозки tcp-соединения тспу (15-20 кб)
public interface IAntiFreezeTransport
{
    //признак активного режима карусели коротких сокетов hydra (<12 кб на сокет)
    public bool IsHydraCarouselActive { get; }

    //проверка реальной прокачки >20 кб и автоматическое включение карусели hydra при обнаружении заморозки 16 кб
    public Task<bool> VerifyThroughputOrActivateHydraAsync(CancellationToken ct);
}
