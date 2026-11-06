namespace obxodka.Models.Responses;

//ответ сервера с хэшем сертификата для ssl pinning
public sealed class CertHashResponse
{
    //sha256 хэш сертификата сервера
    public string? Hash { get; set; }
}
