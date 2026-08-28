namespace obxodka.Client.Tests;

[Trait("Category", "Unit")]
public class ApiErrorHandlerTests
{
    [Theory]
    [InlineData("SSL connection could not be established", "Ошибка защищенного соединения. Пожалуйста, отключите другой VPN, AdGuard или антивирус, они блокируют трафик.")]
    [InlineData("The remote certificate is invalid", "Ошибка защищенного соединения. Пожалуйста, отключите другой VPN, AdGuard или антивирус, они блокируют трафик.")]
    [InlineData("connection refused", "Нет связи с сервером. Проверьте интернет-соединение или выключите локальный VPN/AdGuard.")]
    [InlineData("timed out", "Нет связи с сервером. Проверьте интернет-соединение или выключите локальный VPN/AdGuard.")]
    [InlineData("502 Bad Gateway", "Сервер временно недоступен (502/504). Пожалуйста, повторите попытку позже.")]
    [InlineData("500 Internal Server Error", "Произошла сбойная ошибка сервера (500). Попробуйте позже.")]
    public void ParseCommonSystemErrorsReturnsClearGuidance(string rawError, string expectedSnippet)
    {
        var result = ApiErrorHandler.ParseLoginError(rawError);
        Assert.Equal(expectedSnippet, result);
    }

    [Fact]
    public void ParseRegistrationErrorExistingEmail()
    {
        var result = ApiErrorHandler.ParseRegistrationError("Этот email уже занят в системе");
        Assert.Equal("Этот Email уже занят.", result);
    }

    [Fact]
    public void ParseRegistrationErrorDuplicateHwid()
    {
        var result = ApiErrorHandler.ParseRegistrationError("duplicate key value violates unique constraint on Hwid (23505)");
        Assert.Equal("На этом устройстве уже зарегистрирован аккаунт. Войдите в него.", result);
    }

    [Fact]
    public void ParseLoginErrorDeviceLimit()
    {
        var result = ApiErrorHandler.ParseLoginError("device limit exceeded");
        Assert.Equal("Лимит устройств исчерпан (макс. 3)", result);
    }

    [Fact]
    public void ParseLoginErrorBanned()
    {
        var result = ApiErrorHandler.ParseLoginError("account is banned");
        Assert.Equal("Ваш аккаунт заблокирован", result);
    }

    [Fact]
    public void ParseJsonErrorMessage()
    {
        var rawJson = /*lang=json,strict*/ "{\"message\":\"Слишком много попыток. Попробуйте через минуту.\"}";
        var result = ApiErrorHandler.ParseGeneralError(rawJson);
        Assert.Equal("Слишком много попыток. Попробуйте через минуту.", result);
    }

    [Fact]
    public void ParseEmptyOrNullReturnsFallback()
    {
        var result = ApiErrorHandler.ParseGeneralError(null, "Запасное сообщение");
        Assert.Equal("Запасное сообщение", result);
    }
}
