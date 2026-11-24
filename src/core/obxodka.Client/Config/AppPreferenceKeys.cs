namespace obxodka.Config;

//ключи для сохранения настроек приложения (значения не менять для сохранения совместимости)
public static class AppPreferenceKeys
{
    //настройки VPN-туннеля и маршрутизации
    public const string AutoReconnect = "AutoReconnect";
    public const string KillSwitch = "KillSwitch";
    public const string ForceWebSocket = "ForceWebSocket";
    public const string BlockIpv6 = "BlockIpv6";
    public const string BypassRuSites = "obxodka_win_preset_rutlds";
    public const string SyncTimeZoneWithServer = "sync_timezone_with_server";
    public const string OriginalUserTimeZoneId = "original_user_timezone_id";

    //настройки приложения, DNS-фильтра и телеметрии
    public const string RunOnStartup = "RunOnStartup";
    public const string UseAdblockDns = "use_adblock_dns";
    public const string AdblockLastSyncTicks = "adblock_last_sync_ticks";
    public const string UseTelemetry = "use_telemetry";

    //сессия пользователя, подписка и локальный кэш
    public const string UserEmail = "user_email";
    public const string UserIsLogged = "user_is_logged";
    public const string UserSubUntil = "user_sub_until";
    public const string UserBalanceSec = "user_balance_sec";
    public const string UserPassword = "user_password";
    public const string UserJwt = "user_jwt";
    public const string UserVpnConfig = "user_vpn_config";
    public const string HwidCache = "hwid_cache";
    public const string CachedClusterServersJson = "cached_cluster_servers_json";
}
