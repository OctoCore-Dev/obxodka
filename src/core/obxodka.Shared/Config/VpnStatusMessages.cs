namespace obxodka.Shared.Config;

public static class VpnStatusMessages
{
    public const string Disconnected = "Не в сети";
    public const string Connected = "В сети";
    public const string Protected = "Защищено";
    public const string Connecting = "Подключение...";
    public const string Disconnecting = "Отключение...";
    public const string PleaseWait = "ЖДИТЕ";
    public const string Retry = "ПОВТОРИТЬ";
    public const string StartAction = "СТАРТ";
    public const string StopAction = "СТОП";
    public const string ConnectAction = "ПОДКЛЮЧИТЬ";
    public const string DisconnectAction = "ОТКЛЮЧИТЬ";
    public const string IpNotAssigned = "Не назначен";
    public const string IpAcquiring = "Получение...";
    public const string DefaultConnectionError = "Ошибка подключения";

    public const string CleaningOldSettings = "Очистка старых сетевых настроек...";
    public const string ResolvingFastestNode = "Поиск быстрейшего узла (Happy Eyeballs)...";
    public const string InitializingWintunAdapter = "Инициализация виртуального адаптера Wintun...";
    public const string ApplyingNetworkSettings = "Применение настроек сети...";
    public const string CheckingTunnelReadiness = "Проверка готовности туннеля (TX/RX)...";
    public const string CheckingChannelDuplex = "Проверка канала (отправка и приём)...";
    public const string TxTransmissionFailed = "Сбой исходящей передачи (TX=0). Проверьте сокет или локальный фаервол.";
    public const string RedirectingTraffic = "Перенаправление трафика в туннель...";
    public const string EnablingDnsProtection = "Включение защиты от утечек DNS...";
    public const string ProtectedConnectionActive = "Защищенное соединение активно.";
    public const string CheckingInternetAndDns = "Проверка доступа в интернет и DNS...";
    public const string MainNodeUnavailableDirectly = "Основной узел недоступен напрямую, пробуем резервные пути...";
    public const string DnsFailureDetected = "Обнаружен сбой системного DNS. Автовосстановление серверов...";
    public const string DnsRestoredSuccessfully = "Системный DNS успешно восстановлен (Яндекс / Cloudflare).";
    public const string SwitchingToAutonomousDns = "Обходка переключается на автономный защищённый DNS.";
    public const string InternetAndDnsHealthy = "Интернет и системный DNS в порядке.";
    public const string DisconnectingVpn = "Отключение VPN...";
    public const string RestoringNetworkSettings = "Восстановление сетевых настроек и DNS...";
    public const string VpnDisconnectedSystem = "[SYSTEM] VPN отключён.";

    public const string PacketLossDetected = "[SMART CONNECT] Обнаружена потеря пакетов. Попытка восстановления маршрута...";
    public const string PacketBlockDetected = "[SMART CONNECT] Обнаружена блокировка передачи пакетов. Автопереключение...";
    public const string ConnectionRestored = "[SMART CONNECT] Соединение восстановлено!";
    public const string ConnectionRestoredTls = "[SMART CONNECT] Соединение восстановлено через TCP/TLS!";
    public const string SwitchedToBackupNodeSuccess = "[SMART CONNECT] Подключение успешно переведено на новый узел!";
    public const string SwitchedToBackupServerSuccess = "[SMART CONNECT] Подключение успешно переведено на новый сервер!";

    public static string BuildingRoute(string host) => $"Построение маршрута через {host}...";
    public static string DomainRoute(string domain) => $"[DOMAINS] Маршрутизация через: {domain}";
    public static string ConnectingToNode(string host, int port) => $"Подключение к {host}:{port}...";
    public static string ConnectingToServer(string host, int port) => $"Подключение к серверу {host}:{port}...";
    public static string IpAssigned(string ip) => $"Получен IP: {ip}";
    public static string InitializingAdapter(string adapterName) => $"Инициализация адаптера ({adapterName})...";
    public static string StartingAdapter(string adapterName) => $"Запуск адаптера ({adapterName})...";
    public static string ReconnectingAttempt(int attempt, int max = 2) => $"Повтор подключения ({attempt}/{max})...";
    public static string SwitchingToBackupNode(string node) => $"[SMART CONNECT] Переключение на резервный узел: {node}...";
    public static string SwitchingToBackupServer(string server) => $"[SMART CONNECT] Переключение на резервный сервер: {server}...";
    public static string NodeUnavailableTryingBackup(string node) => $"Сервер {node} недоступен или нет трафика. Пробуем запасной узел...";
    public static string ServerUnavailableTryingBackup(string server) => $"Сервер {server} недоступен или нет трафика. Пробуем запасной сервер...";
    public static string NoIncomingTraffic(int attempt, int max = 2) => $"Нет входящего трафика от сервера (0 RX). Переподключение ({attempt}/{max})...";
    public static string RxResponseTimeout(long txBytes) => $"Сервер не отвечает на запросы (отправлено {txBytes} Б, получено 0 Б).";
    public static string ThirdPartyVpnWarning(string vpnName) => $"[ВНИМАНИЕ] Обнаружен активный сторонний VPN: '{vpnName}'. Пожалуйста, отключите его!";

    public static string ScanningMtuHole(int maxMtu, int minMtu, int step = 8) => $"Поиск дыры MTU ({maxMtu} -> {minMtu} Б, шаг {step})...";
    public static string ProbingMtuHole(int maxMtu, int minMtu, int cycle) => $"Поиск дыры MTU (проход #{cycle}: {maxMtu} -> {minMtu} Б)...";
    public static string ProbingDuplex(int maxMtu, int minMtu, int cycle, long txBytes, long rxBytes) => $"Поиск MTU (#{cycle}, {maxMtu}->{minMtu} Б): отправка {txBytes} Б, приём {rxBytes} Б...";
    public static string TestingMtuCandidate(int mtu) => $"Тестирование MTU: {mtu} Б...";
    public static string MtuHoleFound(int mtu) => $"Найдена дыра MTU: {mtu} Б";
    public static string MtuHoleFoundWithPing(int mtu, long rttMs) => $"Найдена дыра MTU: {mtu} Б (пинг {rttMs} мс)";
    public static string MtuSyncingWithServer(int mtu) => $"Синхронизация MTU с сервером ({mtu} Б)...";
    public static string MtuLocked(int mtu) => $"MTU зафиксирован: {mtu} Б";
    public static string PingReceived(long rttMs) => $"Пинг получен (RTT={rttMs} мс)";
    public static string DownlinkChecking(long rxBytes) => $"Проверка соединения с сервером (получено {rxBytes} Б)...";
    public static string DownlinkCheckingDuplex(long txBytes, long rxBytes) => $"Проверка канала (отправлено {txBytes} Б, получено {rxBytes} Б)...";
    public static string DownlinkVerified(long rxBytes) => $"Связь подтверждена (получено {rxBytes} Б)! Активация...";
    public static string ChannelVerifiedDuplex(long txBytes, long rxBytes) => $"Связь подтверждена (отправлено {txBytes} Б, получено {rxBytes} Б)! Активация...";
    public static string ChannelVerifiedSecureDuplex(long txBytes, long rxBytes) => $"Связь подтверждена (отправлено {txBytes} Б, получено {rxBytes} Б)! Защищенное соединение установлено.";
    public static string DownlinkVerifiedSecure(long rxBytes) => $"Связь подтверждена (получено {rxBytes} Б)! Защищенное соединение установлено.";
    public static string ConnectionFailed(string reason) => $"Ошибка: {reason}";
}
