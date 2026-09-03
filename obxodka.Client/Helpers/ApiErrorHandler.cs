namespace obxodka.Helpers;

public static class ApiErrorHandler
{
    private static string ParseCommonSystemError(ReadOnlySpan<char> error)
    {
        return ContainsAny(error, ["SSL connection could not be established", "SslHandshakeException", "The remote certificate is invalid", "Schannel"])
            ? "Ошибка защищенного соединения. Пожалуйста, отключите другой VPN, AdGuard или антивирус, они блокируют трафик."
            : ContainsAny(error, [
                "host is known", "unreachable", "socket", "connection refused", "timed out", "canceled", "unresolved",
                "отклонил", "разорвал", "неизвестен", "тайм-аут", "недоступен", "подключение"
            ])
            ? "Нет связи с сервером. Проверьте интернет-соединение или выключите локальный VPN/AdGuard."
            : ContainsAny(error, ["<html", "<!DOCTYPE", "Bad Gateway", "502", "504"])
            ? "Сервер временно недоступен (502/504). Пожалуйста, повторите попытку позже."
            : ContainsAny(error, ["500", "Internal Server Error"])
            ? "Произошла сбойная ошибка сервера (500). Попробуйте позже."
            : string.Empty;
    }

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
            : ContainsAny(span, ["занят", "уже существует"])
            ? "Этот Email уже занят."
            : ContainsAny(span, ["unique constraint", "duplicate key", "Hwid", "23505"])
            ? "На этом устройстве уже зарегистрирован аккаунт. Войдите в него."
            : ExtractMessageFromJson(error, "Неизвестная ошибка регистрации");
    }

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
            : ContainsAny(span, ["Unauthorized", "401"])
            ? "Аккаунт не найден или пароль неверный"
            : ContainsAny(span, ["limit", "device"])
            ? "Лимит устройств исчерпан (макс. 3)"
            : ContainsAny(span, ["banned"])
            ? "Ваш аккаунт заблокирован"
            : ExtractMessageFromJson(error, $"Ошибка авторизации: {error}");
    }

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

    private static bool ContainsAny(ReadOnlySpan<char> source, params ReadOnlySpan<string> keywords)
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

    private static string ExtractMessageFromJson(string error, string fallbackMessage)
    {
        if (!error.Contains("message", StringComparison.OrdinalIgnoreCase))
        {
            return fallbackMessage;
        }

        try
        {
            using var json = JsonDocument.Parse(error);
            if (json.RootElement.TryGetProperty("message", out var msgProp) && msgProp.GetString() is { } msg)
            {
                return msg;
            }
        }
        catch
        {
        }

        return fallbackMessage;
    }
}
