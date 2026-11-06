namespace obxodka.Client.Localization;

//пользовательские сообщения и тексты ошибок управления аккаунтом и устройствами
public static class AccountMessages
{
    public const string LoadDevicesFailed = "Не удалось загрузить список устройств.";
    public const string ConnectionProblem = "Проблема с соединением.";
    public const string CannotDeleteCurrentDevice = "Нельзя удалить текущее устройство.";
    public const string DeleteDeviceTitle = "Удаление";
    public const string DeleteDeviceConfirm = "Удалить это устройство?";
    public const string RemoveDeviceFailed = "Не удалось удалить устройство.";
    public const string ActiveNow = "Активно сейчас";

    public const string LoginRequired = "Сначала войдите в систему.";
    public const string DeleteAccountConfirm = "Удалить аккаунт навсегда? Это действие необратимо.";
    public const string DeleteAccountSuccess = "Аккаунт успешно удален.";
    public const string DeleteAccountFailed = "Не удалось удалить аккаунт.";
    public const string NoServerConnection = "Нет связи с сервером.";
    public const string DeletingProgress = "УДАЛЕНИЕ...";
    public const string DeleteAccountButton = "УДАЛИТЬ АККАУНТ";

    public const string ReferralCopiedTitle = "Скопировано";
    public const string ReferralCodePrompt = "Введите промокод или реферальный код друга.";
    public const string PromoActivatedSuccess = "Код успешно активирован!";
    public const string PromoActivateFailed = "Не удалось активировать код.";
    public const string AdBlockDisabled = "Фильтрация рекламы и трекеров отключена";

    public const string AttentionTitle = "Внимание";
    public const string SuccessTitle = "Успех";
    public const string ErrorTitle = "Ошибка";
    public const string ButtonOk = "OK";
    public const string ButtonCancel = "Отмена";
    public const string ButtonConfirmYes = "Да";
    public const string ButtonConfirmDelete = "Да, удалить";

    public static string ReferralCopiedMessage(string code) => $"Ваш реферальный код {code} скопирован в буфер обмена.";
}
