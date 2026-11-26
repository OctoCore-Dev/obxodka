namespace obxodka.Config;

//эндпоинты для проверки связи пинга и замера скорости
public static class DiagnosticEndpoints
{
    //быстрые адреса для проверки доступности сети
    public static readonly string[] PingEndpoints =
    [
        "https://ya.ru/favicon.ico",
        "https://77.88.8.8/",
        "https://vk.com/favicon.ico"
    ];

    //файлы для замера скорости скачивания
    public static readonly string[] SpeedTestDownloadCandidates =
    [
        "https://speedtest.selectel.ru/100MB",
        "https://mirror.yandex.ru/debian/ls-lR.gz"
    ];
}
