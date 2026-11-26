namespace obxodka.Core.Transports;

//дополнительный контракт транспорта для обратной связи о фактическом удаленном ip и его блокировке
public interface IHealthAwareTransport
{
    //фактический ip удаленного сокета текущего подключения
    string? RemoteConnectedEndpointIp { get; }

    //пометка текущего удаленного ip как проблемного (0 rx / зависание тспу) для ротации в обход
    void ReportUnhealthyEndpoint();
}

//контракт транспорта со встроенной защитой от пороговой заморозки tcp-соединения тспу (15-20 кб)
public interface IAntiFreezeTransport
{
    //признак активного режима карусели коротких сокетов hydra (<12 кб на сокет)
    bool IsHydraCarouselActive { get; }

    //проверка реальной прокачки >20 кб и автоматическое включение карусели hydra при обнаружении заморозки 16 кб
    Task<bool> VerifyThroughputOrActivateHydraAsync(CancellationToken ct);
}
