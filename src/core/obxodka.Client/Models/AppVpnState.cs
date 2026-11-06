namespace obxodka.Models;

//состояние vpn соединения для отображения в интерфейсе
public enum AppVpnState
{
    //соединение разорвано
    Disconnected,

    //идет процесс подключения
    Connecting,

    //туннель активен
    Connected,

    //ошибка подключения
    Error,

    //повторное автоматическое переподключение
    Reconnecting,

    //идет процесс отключения
    Disconnecting
}
