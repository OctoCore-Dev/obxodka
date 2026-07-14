namespace obxodka.Helpers;

public static class ApiErrorHandler
{
    private static string ParseCommonSystemError(string error) =>
        error.Contains("host is known", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("unreachable", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("socket", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("canceled", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("unresolved", StringComparison.OrdinalIgnoreCase)
            ? "Нет связи с сервером. Проверьте интернет-соединение."
            : error.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
              error.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
              error.Contains("Bad Gateway", StringComparison.OrdinalIgnoreCase) ||
              error.Contains("502") ||
              error.Contains("504")
                ? "Сервер временно недоступен (502/504). Пожалуйста, повторите попытку позже."
                : error.Contains("500") || error.Contains("Internal Server Error", StringComparison.OrdinalIgnoreCase)
                    ? "Произошла сбойная ошибка сервера (500). Попробуйте позже."
                    : string.Empty;

    public static string ParseRegistrationError(string? error)
    {
        if (string.IsNullOrEmpty(error))
        {
            return "Ошибка сервера. Попробуйте позже.";
        }

        var commonError = ParseCommonSystemError(error);
        return !string.IsNullOrEmpty(commonError)
            ? commonError
            : error.Contains("занят", StringComparison.OrdinalIgnoreCase) ||
              error.Contains("уже существует", StringComparison.OrdinalIgnoreCase)
                ? "Этот Email уже занят."
                : error.Contains("unique constraint", StringComparison.OrdinalIgnoreCase) ||
                  error.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) ||
                  error.Contains("Hwid", StringComparison.OrdinalIgnoreCase) ||
                  error.Contains("23505")
                    ? "На этом устройстве уже зарегистрирован аккаунт. Войдите в него."
                    : ExtractMessageFromJson(error, "Неизвестная ошибка регистрации");
    }

    public static string ParseLoginError(string? error)
    {
        if (string.IsNullOrEmpty(error))
        {
            return "Неверная почта или пароль";
        }

        var commonError = ParseCommonSystemError(error);
        return !string.IsNullOrEmpty(commonError)
            ? commonError
            : error.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
              error.Contains("401")
                ? "Аккаунт не найден или пароль неверный"
                : error.Contains("limit", StringComparison.OrdinalIgnoreCase) ||
                  error.Contains("device", StringComparison.OrdinalIgnoreCase)
                    ? "Лимит устройств исчерпан (макс. 3)"
                    : error.Contains("banned", StringComparison.OrdinalIgnoreCase)
                        ? "Ваш аккаунт заблокирован"
                        : ExtractMessageFromJson(error, "Ошибка авторизации");
    }

    public static string ParseGeneralError(string? error, string fallbackMessage = "Произошла непредвиденная ошибка")
    {
        if (string.IsNullOrEmpty(error))
        {
            return fallbackMessage;
        }

        var commonError = ParseCommonSystemError(error);
        return !string.IsNullOrEmpty(commonError) ? commonError : ExtractMessageFromJson(error, fallbackMessage);
    }

    private static string ExtractMessageFromJson(string error, string fallbackMessage)
    {
        if (error.Contains("message", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var json = JsonDocument.Parse(error);
                if (json.RootElement.TryGetProperty("message", out var msgProp))
                {
                    return msgProp.GetString() ?? fallbackMessage;
                }
            }
            catch
            {
                // Ignore parsing errors for non-JSON strings
            }
        }
        return fallbackMessage;
    }
}
