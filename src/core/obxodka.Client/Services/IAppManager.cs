namespace obxodka.Services;

//интерфейс менеджера установленных приложений для раздельного туннелирования
public interface IAppManager
{
    //список установленных программ
    Task<List<AppInfoItem>> GetInstalledAppsAsync();

    //список пакетов в обход vpn
    List<string> GetBypassedPackages();

    //сохранение списка пакетов в обход vpn
    void SaveBypassedPackages(List<string> packages);
}
