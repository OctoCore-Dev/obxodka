namespace obxodka.Models;

//данные текущей сессии авторизованного пользователя
public sealed class UserSession
{
    //почта пользователя
    public string? Email { get; set; }

    //пароль или код доступа
    public string? Password { get; set; }

    //jwt токен авторизации
    public string? JwtToken { get; set; }

    //флаг успешного входа
    public bool IsLoggedIn { get; set; }

    //зашифрованная конфигурация vpn
    public string? VpnConfig { get; set; }

    //дата окончания активной подписки
    public DateTime? SubscriptionUntil { get; set; }

    //оставшийся баланс в секундах
    public long BalanceSeconds { get; set; }
}
