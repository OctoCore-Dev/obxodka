namespace obxodka.Models;

//отправка кода на почту для авторизации
public sealed record AuthRequest(
    string Email,
    string Password,
    string Hwid,
    string? DeviceName);

public sealed record GoogleAuthRequest(
    string IdToken,
    string Hwid,
    string? DeviceName);

public sealed record EmailAuthRequest(string Email);

//подтверждение кода на почту для авторизации и данные
public sealed record EmailVerifyRequest(
    string Email,
    string Code,
    string Hwid,
    string? DeviceName);

//результат валидации email адреса с описанием ошибки и подсказкой опечатки
public readonly record struct EmailValidationResult(
    bool IsValid,
    string? ErrorMessage = null,
    string? SuggestedCorrection = null,
    string? NormalizedEmail = null);

//получение ответа на авторизацию и данные
public sealed record LoginResponse(
    string Token,
    string VpnConfig,
    string CertThumbprint,
    DateTime? SubscriptionUntil,
    long BalanceSeconds = 0,
    string Email = "");

//получение инфы о устройствах
public sealed record DeviceItem(
    string? Hwid,
    string? Name,
    DateTime LastActive)
{
    [JsonIgnore]
    public bool IsCurrentDevice { get; set; }

    public string LastActiveText => IsCurrentDevice
        ? "Активно сейчас"
        : $"Активен: {LastActive.ToLocalTime():dd.MM.yyyy HH:mm}";

    public string DeviceIcon => (Name?.ToLowerInvariant() ?? string.Empty) switch
    {
        var s when s.Contains("windows", StringComparison.OrdinalIgnoreCase) ||
                   s.Contains("desktop", StringComparison.OrdinalIgnoreCase) ||
                   s.Contains("laptop", StringComparison.OrdinalIgnoreCase) ||
                   s.Contains("pc", StringComparison.OrdinalIgnoreCase) => "Desktop",
        var s when s.Contains("mac", StringComparison.OrdinalIgnoreCase) ||
                   s.Contains("imac", StringComparison.OrdinalIgnoreCase) ||
                   s.Contains("macbook", StringComparison.OrdinalIgnoreCase) => "Mac",
        var s when s.Contains("iphone", StringComparison.OrdinalIgnoreCase) ||
                   s.Contains("ipad", StringComparison.OrdinalIgnoreCase) ||
                   s.Contains("ios", StringComparison.OrdinalIgnoreCase) => "iOS",
        var s when s.Contains("android", StringComparison.OrdinalIgnoreCase) ||
                   s.Contains("samsung", StringComparison.OrdinalIgnoreCase) ||
                   s.Contains("pixel", StringComparison.OrdinalIgnoreCase) => "Android",
        var s when s.Contains("linux", StringComparison.OrdinalIgnoreCase) ||
                   s.Contains("ubuntu", StringComparison.OrdinalIgnoreCase) => "Linux",
        _ => "Phone"
    };
}

//пользователь потратил n количество байтов за определенное время
public sealed record TrafficReportDto(string Thumbprint, long Bytes);

//запись пачек n количество байтов n количетсво пользоватлей
public sealed record SyncRequestDto(
    List<TrafficReportDto> Active,
    List<string> Disconnected);

//получение инфы о пользователе с сервера
public sealed record UserProfileResponse(DateTime? SubscriptionUntil, long TotalBytesUsed, long BalanceSeconds = 0);

//получение списков сервера
public sealed record VpnServerDto(
    string Ip,
    int Port,
    string Location,
    bool IsOnline,
    int LoadPercent,
    string? CertHash = null,
    string? Provider = "Vultr",
    string? Flag = null,
    string? Role = "worker",
    string[]? Mirrors = null)
{
    public bool Equals(VpnServerDto? other) =>
        ReferenceEquals(this, other) ||
        (other is not null &&
         Ip == other.Ip &&
         Port == other.Port &&
         Location == other.Location &&
         IsOnline == other.IsOnline &&
         LoadPercent == other.LoadPercent &&
         CertHash == other.CertHash &&
         Provider == other.Provider &&
         Flag == other.Flag &&
         Role == other.Role &&
         ((Mirrors is null && other.Mirrors is null) ||
          (Mirrors is not null && other.Mirrors is not null && Mirrors.AsSpan().SequenceEqual(other.Mirrors))));

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Ip);
        hash.Add(Port);
        hash.Add(Location);
        hash.Add(IsOnline);
        hash.Add(LoadPercent);
        hash.Add(CertHash);
        hash.Add(Provider);
        hash.Add(Flag);
        hash.Add(Role);
        if (Mirrors is not null)
        {
            for (var i = 0; i < Mirrors.Length; i++)
            {
                hash.Add(Mirrors[i]);
            }
        }
        return hash.ToHashCode();
    }
}

//телеметрия ошибок, таймаутов подключения и крашей туннеля
public sealed record TelemetryDto(
    string Hwid,
    string AppVersion,
    string Message,
    string StackTrace,
    string? Email = null);

// сообщение
public sealed record MessageResponse([property: JsonPropertyName("message")] string Message);

//получение ссылки для оплаты
public sealed record PaymentLinkResponse([property: JsonPropertyName("url")] string Url);

//создание инвойса для внешних сервисов и оплаты
public sealed record CreateInvoiceRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("offerId")] string? OfferId = null,
    [property: JsonPropertyName("returnUrl")] string? ReturnUrl = null,
    [property: JsonPropertyName("autoCreateUser")] bool AutoCreateUser = false);

// запрос от клиента к серверу
public sealed record GooglePurchaseVerifyRequest(string ProductId, string PurchaseToken, string? OrderId);
//ответ от сервера к клиенту
public sealed record GooglePurchaseVerifyResponse(bool Success, int SecondsAdded = 0, long NewBalance = 0, string? Message = null);

//отзывы
public sealed record PostReviewRequest(string Text, int? Rating, Guid? ParentId);
public sealed record PostReviewResponse(string Message, Guid Id);
public sealed record ReviewReplyDto(Guid Id, string? Author, string Text, DateTime CreatedAt);
public sealed record ReviewDto(Guid Id, string? Author, string Text, int? Rating, int Likes, DateTime CreatedAt, List<ReviewReplyDto> Replies);
public sealed record LikeResponse(int Likes);

//реферальная ссылка друга для приглашение
public sealed record ReferralFriendDto(string EmailMasked, DateTime ActivatedAt, int BonusHours);
public sealed record ReferralCodeResponse(string Code, int ActivatedCount, long BalanceSeconds, List<ReferralFriendDto> Friends);
public sealed record ActivateReferralRequest(string Code);

//диагностика и подключение
public sealed record IncidentReportDto(
    string? Email,
    string? Hwid,
    string? ClientVersion,
    string? Platform,
    string? ClientIp,
    string? TargetServerIp,
    string? TargetHost,
    int EffectivePort,
    string Reason,
    string DiagnosticSteps,
    int AttemptsCount,
    int LastObservedMtu,
    long Ray0Rtt,
    long Ray1Rtt,
    long Ray2Rtt,
    bool Ray0Blackholed,
    bool Ray1Blackholed,
    bool Ray2Blackholed,
    DateTime TimestampUtc);
