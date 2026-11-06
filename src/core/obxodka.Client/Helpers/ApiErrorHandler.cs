namespace obxodka.Helpers;

//обработка и перевод ошибок api и сетевых сбоев для отображения пользователю
public static class ApiErrorHandler
{
    private static readonly string[] s_sslErrors =
    [
        "SSL connection could not be established",
        "SslHandshakeException",
        "The remote certificate is invalid",
        "Schannel"
    ];

    private static readonly string[] s_networkErrors =
    [
        "host is known", "unreachable", "socket", "connection refused", "timed out", "canceled", "unresolved",
        "отклонил", "разорвал", "неизвестен", "тайм-аут", "недоступен", "подключение"
    ];

    private static readonly string[] s_badGatewayErrors = ["<html", "<!DOCTYPE", "Bad Gateway", "502", "504"];
    private static readonly string[] s_serverErrors = ["500", "Internal Server Error"];

    private static readonly string[] s_registrationTakenKeywords = ["занят", "уже существует"];
    private static readonly string[] s_registrationDuplicateDeviceKeywords = ["unique constraint", "duplicate key", "Hwid", "23505"];

    private static readonly string[] s_unauthorizedKeywords = ["Unauthorized", "401"];
    private static readonly string[] s_deviceLimitKeywords = ["limit", "device"];
    private static readonly string[] s_bannedKeywords = ["banned"];

    //разбор общих системных ошибок сокетов и tls
    private static string ParseCommonSystemError(ReadOnlySpan<char> error)
    {
        return ContainsAny(error, s_sslErrors)
            ? "Ошибка защищенного соединения. Пожалуйста, отключите другой VPN, AdGuard или антивирус, они блокируют трафик."
            : ContainsAny(error, s_networkErrors)
            ? "Нет связи с сервером. Проверьте интернет-соединение или выключите локальный VPN/AdGuard."
            : ContainsAny(error, s_badGatewayErrors)
            ? "Сервер временно недоступен (502/504). Пожалуйста, повторите попытку позже."
            : ContainsAny(error, s_serverErrors)
            ? "Произошла сбойная ошибка сервера (500). Попробуйте позже."
            : string.Empty;
    }

    //разбор ошибок регистрации пользователя
    public static string ParseRegistrationError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "Ошибка сервера. Попробуйте позже.";
        }

        var span = error.AsSpan();
        var commonError = ParseCommonSystemError(span);
        return !string.IsNullOrEmpty(commonError)
            ? commonError
            : ContainsAny(span, s_registrationTakenKeywords)
            ? "Этот Email уже занят."
            : ContainsAny(span, s_registrationDuplicateDeviceKeywords)
            ? "На этом устройстве уже зарегистрирован аккаунт. Войдите в него."
            : ExtractMessageFromJson(error, "Неизвестная ошибка регистрации");
    }

    //разбор ошибок входа в систему
    public static string ParseLoginError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "Неверная почта или пароль";
        }

        var span = error.AsSpan();
        var commonError = ParseCommonSystemError(span);
        return !string.IsNullOrEmpty(commonError)
            ? commonError
            : ContainsAny(span, s_unauthorizedKeywords)
            ? "Аккаунт не найден или пароль неверный"
            : ContainsAny(span, s_deviceLimitKeywords)
            ? "Лимит устройств исчерпан (макс. 3)"
            : ContainsAny(span, s_bannedKeywords)
            ? "Ваш аккаунт заблокирован"
            : ExtractMessageFromJson(error, $"Ошибка авторизации: {error}");
    }

    //разбор общих ошибок запроса с запасным сообщением
    public static string ParseGeneralError(string? error, string fallbackMessage = "Произошла непредвиденная ошибка")
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return fallbackMessage;
        }

        var commonError = ParseCommonSystemError(error.AsSpan());
        return !string.IsNullOrEmpty(commonError)
            ? commonError
            : ExtractMessageFromJson(error, fallbackMessage);
    }

    //проверка наличия ключевых слов в строке
    private static bool ContainsAny(ReadOnlySpan<char> source, ReadOnlySpan<string> keywords)
    {
        foreach (var keyword in keywords)
        {
            if (source.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    //извлечение текста ошибки из json ответа
    private static string ExtractMessageFromJson(string error, string fallbackMessage)
    {
        var trimmed = error.TrimStart();
        if (!trimmed.StartsWith('{'))
        {
            return fallbackMessage;
        }

        try
        {
            using var json = JsonDocument.Parse(error);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("message", out var msgProp) && msgProp.GetString() is { Length: > 0 } msg)
                {
                    return msg;
                }

                if (root.TryGetProperty("error", out var errProp) && errProp.GetString() is { Length: > 0 } err)
                {
                    return err;
                }

                if (root.TryGetProperty("title", out var titleProp) && titleProp.GetString() is { Length: > 0 } title)
                {
                    return title;
                }
            }
        }
        catch
        {
        }

        return fallbackMessage;
    }
}
