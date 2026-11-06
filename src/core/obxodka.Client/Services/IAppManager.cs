namespace obxodka.Services;

//интерфейс менеджера установленных приложений для раздельного туннелирования
public interface IAppManager
{
    //список установленных программ
    public Task<List<AppInfoItem>> GetInstalledAppsAsync();

    //список пакетов в обход vpn
    public List<string> GetBypassedPackages();

    //сохранение списка пакетов в обход vpn
    public void SaveBypassedPackages(List<string> packages);
}
