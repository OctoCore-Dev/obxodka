namespace obxodka.Models;

//статистика скорости входящего и исходящего трафика
public sealed class AppTrafficStats
{
    //скорость загрузки байт в секунду
    public long DownloadSpeedBps { get; init; }

    //скорость отдачи байт в секунду
    public long UploadSpeedBps { get; init; }
}
