namespace obxodka.Client.Localization;

//пользовательские сообщения и тексты ошибок экрана авторизации
public static class AuthMessages
{
    public const string QrDesktopHint = "Сканирование QR-кода камерой поддерживается на Android. На ПК используйте 6-значный код или вставьте ссылку из буфера.";
    public const string QrInvalidPayload = "QR-код не содержит данных авторизации Обходка VPN.";
    public const string QrScanFailed = "Не удалось отсканировать QR-код.";
    public const string ClipboardEmpty = "Буфер обмена пуст.";
    public const string ClipboardNoCode = "В буфере обмена не найден 6-значный код.";
    public const string ClipboardReadFailed = "Не удалось прочитать буфер обмена.";
    public const string EnterValidEmail = "Пожалуйста, введите корректный Email.";
    public const string EnterSixDigitCode = "Пожалуйста, введите 6-значный код.";
    public const string EmailRequired = "Email не указан. Вернитесь назад.";
    public const string ConnectionError = "Ошибка соединения с сервером.";
    public const string CodeVerificationFailed = "Не удалось проверить код.";
    public const string GetCodeButton = "Получить код";
    public const string VerifyCodeButton = "Войти";
}
