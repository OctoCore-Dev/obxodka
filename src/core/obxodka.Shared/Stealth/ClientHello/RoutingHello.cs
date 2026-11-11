using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace obxodka.Shared.Stealth.ClientHello;

//маршрутизатор, анализатор и генератор отпечатков tls clienthello и браузерных сигнатур
public static class RoutingHello
{
    //лестница поэтапной ротации резервных браузеров для пробития фильтрации тспу
    private static readonly TlsBrowserProfile[] s_defaultRotationLadder =
    [
        TlsBrowserProfile.Chrome,
        TlsBrowserProfile.Yandex,
        TlsBrowserProfile.Firefox,
        TlsBrowserProfile.SafariIos,
        TlsBrowserProfile.Edge,
        TlsBrowserProfile.ChromePostQuantum
    ];

    //набор шифров семейства chromium (chrome, yandex, edge, opera)
    private static readonly TlsCipherSuite[] s_chromiumTlsCipherSuites =
    [
        TlsCipherSuite.TLS_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256
    ];

    //набор шифров mozilla firefox (приоритет chacha20-poly1305 на втором месте)
    private static readonly TlsCipherSuite[] s_firefoxTlsCipherSuites =
    [
        TlsCipherSuite.TLS_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384
    ];

    //набор шифров apple safari (группировка ecdsa перед rsa)
    private static readonly TlsCipherSuite[] s_safariTlsCipherSuites =
    [
        TlsCipherSuite.TLS_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256
    ];

    //получение профиля браузера для заданного номера попытки по лестнице ротации
    public static TlsBrowserProfile GetProfileForAttempt(int attemptIndex) =>
        s_defaultRotationLadder[Math.Abs(attemptIndex) % s_defaultRotationLadder.Length];

    //возвращает упорядоченный список шифров tls под конкретный профиль браузера
    public static TlsCipherSuite[] GetCipherSuites(TlsBrowserProfile profile) => profile switch
    {
        TlsBrowserProfile.Chrome or TlsBrowserProfile.ChromePostQuantum or TlsBrowserProfile.Yandex or TlsBrowserProfile.Edge or TlsBrowserProfile.Opera => s_chromiumTlsCipherSuites,
        TlsBrowserProfile.Firefox => s_firefoxTlsCipherSuites,
        TlsBrowserProfile.SafariIos or TlsBrowserProfile.SafariMac => s_safariTlsCipherSuites,
        _ => s_chromiumTlsCipherSuites
    };

    //определяет режим нарезки пакета clienthello и диапазон микро-джиттера под выбранный браузер
    public static void GetDpiDesyncParams(TlsBrowserProfile profile, out int mode, out int delayMs)
    {
        switch (profile)
        {
            case TlsBrowserProfile.Yandex or TlsBrowserProfile.Opera:
                //нарезка на уровне записей tls record layer (2 валидных рекорда посередине sni)
                mode = 1;
                delayMs = RandomNumberGenerator.GetInt32(12, 24);
                break;
            case TlsBrowserProfile.Firefox or TlsBrowserProfile.SafariIos or TlsBrowserProfile.SafariMac or TlsBrowserProfile.ChromePostQuantum:
                //трехступенчатая гибридная нарезка tls record + l4 tcp посередине sni
                mode = 2;
                delayMs = RandomNumberGenerator.GetInt32(14, 28);
                break;
            case TlsBrowserProfile.Chrome or TlsBrowserProfile.Edge:
            default:
                //классическая l4 tcp сегментация ровно посередине домена sld в sni
                mode = 0;
                delayMs = RandomNumberGenerator.GetInt32(10, 20);
                break;
        }
    }

    //сборка байтового пакета clienthello под конкретный профиль браузера
    public static byte[] BuildHello(TlsBrowserProfile profile, ReadOnlySpan<byte> sni, ReadOnlySpan<byte> sessionId = default) => profile switch
    {
        TlsBrowserProfile.Chrome => ChromiumHello.Build(sni, sessionId, isPostQuantum: false),
        TlsBrowserProfile.ChromePostQuantum => ChromiumHello.Build(sni, sessionId, isPostQuantum: true),
        TlsBrowserProfile.Yandex => ChromiumHello.Build(sni, sessionId, isPostQuantum: false),
        TlsBrowserProfile.Edge => ChromiumHello.Build(sni, sessionId, isPostQuantum: false),
        TlsBrowserProfile.Opera => ChromiumHello.Build(sni, sessionId, isPostQuantum: false),
        TlsBrowserProfile.Firefox => FirefoxHello.Build(sni, sessionId),
        TlsBrowserProfile.SafariIos => SafariHello.Build(sni, sessionId, isMobile: true),
        TlsBrowserProfile.SafariMac => SafariHello.Build(sni, sessionId, isMobile: false),
        _ => ChromiumHello.Build(sni, sessionId, isPostQuantum: false)
    };

    //выбор следующего clienthello по лестнице ротации в зависимости от попытки подключения
    public static byte[] GetNextHello(
        ReadOnlySpan<byte> sni,
        int attemptIndex,
        out TlsBrowserProfile usedProfile)
    {
        usedProfile = GetProfileForAttempt(attemptIndex);
        return BuildHello(usedProfile, sni);
    }

    //быстрая 1-rtt проверка проходимости sni и браузерного отпечатка через тспу до запуска полного стека
    public static async Task<bool> ProbeRawClientHelloAsync(
        EndPoint endPoint,
        string? sni,
        TlsBrowserProfile profile,
        Action<Socket>? protectAction = null,
        int timeoutMs = 1200,
        CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        var token = timeoutCts.Token;

        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        protectAction?.Invoke(socket);

        try
        {
            await socket.ConnectAsync(endPoint, token).ConfigureAwait(false);
            var sniBytes = (!string.IsNullOrWhiteSpace(sni) && !IPAddress.TryParse(sni, out _))
                ? Encoding.ASCII.GetBytes(sni)
                : [];
            var hello = BuildHello(profile, sniBytes);
            var splitIndex = Math.Clamp(hello.Length / 2, 24, Math.Max(24, hello.Length - 16));

            //отправляем первую половину clienthello и после микро-джиттера остаток для обхода dpi
            _ = await socket.SendAsync(hello.AsMemory(0, splitIndex), SocketFlags.None, token).ConfigureAwait(false);
            await Task.Delay(8, token).ConfigureAwait(false);
            _ = await socket.SendAsync(hello.AsMemory(splitIndex), SocketFlags.None, token).ConfigureAwait(false);

            var rx = ArrayPool<byte>.Shared.Rent(32);
            try
            {
                var read = await socket.ReceiveAsync(rx.AsMemory(0, 16), SocketFlags.None, token).ConfigureAwait(false);
                //проверяем что сервер ответил валидной записью tls serverhello (0x16 0x03)
                return read >= 6 && rx[0] == 0x16 && rx[1] == 0x03 && rx[5] == 0x02;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rx);
            }
        }
        catch
        {
            return false;
        }
    }

    //возвращает реалистичный user-agent для выбранного браузерного профиля
    public static string GetUserAgent(TlsBrowserProfile profile) => profile switch
    {
        TlsBrowserProfile.Chrome or TlsBrowserProfile.ChromePostQuantum =>
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36",
        TlsBrowserProfile.Yandex =>
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 YaBrowser/25.2.0.0 Yowser/2.5 Safari/537.36",
        TlsBrowserProfile.Edge =>
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36 Edg/133.0.0.0",
        TlsBrowserProfile.Opera =>
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36 OPR/118.0.0.0",
        TlsBrowserProfile.Firefox =>
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:135.0) Gecko/20100101 Firefox/135.0",
        TlsBrowserProfile.SafariIos =>
            "Mozilla/5.0 (iPhone; CPU iPhone OS 18_3 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.3 Mobile/15E148 Safari/604.1",
        TlsBrowserProfile.SafariMac =>
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.3 Safari/605.1.15",
        _ => "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36"
    };

    //применяет http-заголовки запроса в строгом соответствии с выбранным профилем браузера
    public static void ApplyBrowserHeaders(HttpRequestMessage request, TlsBrowserProfile profile)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ua = GetUserAgent(profile);
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd(ua);
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        if (profile is TlsBrowserProfile.Chrome or TlsBrowserProfile.ChromePostQuantum)
        {
            _ = request.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Not(A:Brand\";v=\"99\", \"Google Chrome\";v=\"133\", \"Chromium\";v=\"133\"");
            _ = request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
            _ = request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        }
        else if (profile is TlsBrowserProfile.Yandex)
        {
            _ = request.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Not(A:Brand\";v=\"99\", \"Chromium\";v=\"133\", \"Yandex\";v=\"25\"");
            _ = request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
            _ = request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        }
        else if (profile is TlsBrowserProfile.Edge)
        {
            _ = request.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Not(A:Brand\";v=\"99\", \"Microsoft Edge\";v=\"133\", \"Chromium\";v=\"133\"");
            _ = request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
            _ = request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        }
        else if (profile is TlsBrowserProfile.Opera)
        {
            _ = request.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Not(A:Brand\";v=\"99\", \"Opera\";v=\"118\", \"Chromium\";v=\"133\"");
            _ = request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
            _ = request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        }

        _ = request.Headers.TryAddWithoutValidation("sec-fetch-dest", "empty");
        _ = request.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
        _ = request.Headers.TryAddWithoutValidation("sec-fetch-site", "same-origin");
    }

    //применяет заголовки websocket-рукопожатия в соответствии с профилем браузера
    public static void ApplyWebSocketHeaders(ClientWebSocketOptions options, TlsBrowserProfile profile)
    {
        ArgumentNullException.ThrowIfNull(options);

        var ua = GetUserAgent(profile);
        options.SetRequestHeader("User-Agent", ua);

        if (profile is TlsBrowserProfile.Chrome or TlsBrowserProfile.ChromePostQuantum)
        {
            options.SetRequestHeader("sec-ch-ua", "\"Not(A:Brand\";v=\"99\", \"Google Chrome\";v=\"133\", \"Chromium\";v=\"133\"");
            options.SetRequestHeader("sec-ch-ua-mobile", "?0");
            options.SetRequestHeader("sec-ch-ua-platform", "\"Windows\"");
        }
        else if (profile is TlsBrowserProfile.Yandex)
        {
            options.SetRequestHeader("sec-ch-ua", "\"Not(A:Brand\";v=\"99\", \"Chromium\";v=\"133\", \"Yandex\";v=\"25\"");
            options.SetRequestHeader("sec-ch-ua-mobile", "?0");
            options.SetRequestHeader("sec-ch-ua-platform", "\"Windows\"");
        }
        else if (profile is TlsBrowserProfile.Edge)
        {
            options.SetRequestHeader("sec-ch-ua", "\"Not(A:Brand\";v=\"99\", \"Microsoft Edge\";v=\"133\", \"Chromium\";v=\"133\"");
            options.SetRequestHeader("sec-ch-ua-mobile", "?0");
            options.SetRequestHeader("sec-ch-ua-platform", "\"Windows\"");
        }
        else if (profile is TlsBrowserProfile.Opera)
        {
            options.SetRequestHeader("sec-ch-ua", "\"Not(A:Brand\";v=\"99\", \"Opera\";v=\"118\", \"Chromium\";v=\"133\"");
            options.SetRequestHeader("sec-ch-ua-mobile", "?0");
            options.SetRequestHeader("sec-ch-ua-platform", "\"Windows\"");
        }
    }

    //автоматическое определение профиля браузера по входящим http-заголовкам запроса
    public static TlsBrowserProfile DetectProfile(string? userAgent, string? secChUa = null)
    {
        var ua = userAgent ?? string.Empty;
        var ch = secChUa ?? string.Empty;

        return ch.Contains("Microsoft Edge", StringComparison.OrdinalIgnoreCase) ||
            ua.Contains(" Edg/", StringComparison.OrdinalIgnoreCase)
            ? TlsBrowserProfile.Edge
            : ch.Contains("Yandex", StringComparison.OrdinalIgnoreCase) ||
            ua.Contains("YaBrowser/", StringComparison.OrdinalIgnoreCase)
            ? TlsBrowserProfile.Yandex
            : ch.Contains("Opera", StringComparison.OrdinalIgnoreCase) ||
            ua.Contains(" OPR/", StringComparison.OrdinalIgnoreCase)
            ? TlsBrowserProfile.Opera
            : ua.Contains("Firefox/", StringComparison.OrdinalIgnoreCase)
            ? TlsBrowserProfile.Firefox
            : (ua.Contains("Safari/", StringComparison.OrdinalIgnoreCase) &&
                !ua.Contains("Chrome/", StringComparison.OrdinalIgnoreCase) &&
                !ua.Contains("Chromium/", StringComparison.OrdinalIgnoreCase))
            ? ((ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || ua.Contains("iPad", StringComparison.OrdinalIgnoreCase))
                ? TlsBrowserProfile.SafariIos
                : TlsBrowserProfile.SafariMac)
            : TlsBrowserProfile.Chrome;
    }

    //возвращает реалистичные заголовки ответа сервера, зеркально адаптированные под профиль браузера
    public static KeyValuePair<string, string>[] GetServerResponseHeaders(TlsBrowserProfile profile) => profile switch
    {
        TlsBrowserProfile.Edge =>
        [
            new("Server", "Microsoft-IIS/10.0"),
            new("X-Content-Type-Options", "nosniff"),
            new("Accept-CH", "Sec-CH-UA, Sec-CH-UA-Mobile, Sec-CH-UA-Platform"),
            new("Strict-Transport-Security", "max-age=31536000; includeSubDomains"),
            new("X-MSEdge-Ref", "Ref A: 0000000000000000 Ref B: LON01 Ref C: 2026-11-02T00:00:00Z")
        ],
        TlsBrowserProfile.Yandex =>
        [
            new("Server", "nginx"),
            new("X-Content-Type-Options", "nosniff"),
            new("Accept-CH", "Sec-CH-UA, Sec-CH-UA-Mobile, Sec-CH-UA-Platform"),
            new("Strict-Transport-Security", "max-age=31536000"),
            new("X-Yandex-Req-Id", "1762060000000000-0000000000000000000-balancer-production")
        ],
        TlsBrowserProfile.Opera =>
        [
            new("Server", "cloudflare"),
            new("X-Content-Type-Options", "nosniff"),
            new("Accept-CH", "Sec-CH-UA, Sec-CH-UA-Mobile, Sec-CH-UA-Platform"),
            new("Alt-Svc", "h3=\":443\"; ma=86400")
        ],
        TlsBrowserProfile.Firefox =>
        [
            new("Server", "nginx"),
            new("X-Content-Type-Options", "nosniff"),
            new("Strict-Transport-Security", "max-age=31536000; includeSubDomains; preload"),
            new("X-Frame-Options", "SAMEORIGIN")
        ],
        TlsBrowserProfile.SafariIos or TlsBrowserProfile.SafariMac =>
        [
            new("Server", "Apple"),
            new("X-Content-Type-Options", "nosniff"),
            new("Strict-Transport-Security", "max-age=31536000; includeSubDomains"),
            new("X-Apple-Orig-Url", "https://apple.com/")
        ],
        TlsBrowserProfile.Chrome or TlsBrowserProfile.ChromePostQuantum =>
        [
            new("Server", "gws"),
            new("X-Content-Type-Options", "nosniff"),
            new("Accept-CH", "Sec-CH-UA, Sec-CH-UA-Mobile, Sec-CH-UA-Platform"),
            new("Alt-Svc", "h3=\":443\"; ma=86400"),
            new("X-XSS-Protection", "0")
        ],
        _ =>
        [
            new("Server", "gws"),
            new("X-Content-Type-Options", "nosniff"),
            new("Accept-CH", "Sec-CH-UA, Sec-CH-UA-Mobile, Sec-CH-UA-Platform"),
            new("Alt-Svc", "h3=\":443\"; ma=86400"),
            new("X-XSS-Protection", "0")
        ]
    };

    //маскировочный json ответ сервера
    public static string GetDecoyJsonResponse(TlsBrowserProfile profile) => profile switch
    {
        TlsBrowserProfile.Edge => /*lang=json,strict*/ "{\"status\":\"ok\",\"service\":\"sync\",\"version\":\"133.0.0.0\",\"engine\":\"EdgeHTML\"}",
        TlsBrowserProfile.Yandex => /*lang=json,strict*/ "{\"status\":\"ok\",\"service\":\"sync\",\"version\":\"25.2.0\",\"engine\":\"YaBrowser\"}",
        TlsBrowserProfile.Opera => /*lang=json,strict*/ "{\"status\":\"ok\",\"service\":\"sync\",\"version\":\"118.0.0.0\",\"engine\":\"Blink\"}",
        TlsBrowserProfile.Firefox => /*lang=json,strict*/ "{\"status\":\"ok\",\"service\":\"sync\",\"version\":\"135.0\",\"engine\":\"Gecko\"}",
        TlsBrowserProfile.SafariIos => /*lang=json,strict*/ "{\"status\":\"ok\",\"service\":\"sync\",\"version\":\"18.3\",\"engine\":\"WebKit\"}",
        TlsBrowserProfile.SafariMac => /*lang=json,strict*/ "{\"status\":\"ok\",\"service\":\"sync\",\"version\":\"18.3\",\"engine\":\"WebKit\"}",
        TlsBrowserProfile.Chrome or TlsBrowserProfile.ChromePostQuantum => /*lang=json,strict*/ "{\"status\":\"ok\",\"service\":\"sync\",\"version\":\"133.0\",\"engine\":\"Blink\"}",
        _ => /*lang=json,strict*/ "{\"status\":\"ok\",\"service\":\"sync\",\"version\":\"133.0\",\"engine\":\"Blink\"}"
    };

    //реалистичный mime тип исходящего запроса под путь эндпоинта
    public static string GetStreamRequestContentType(string? path) => path switch
    {
        "/api/v1/stream" => "video/iso.segment",
        "/api/v1/sync" => "application/grpc-web+proto",
        "/v1/telemetry" or "/v2/event" => "application/x-protobuf",
        "/cdn-cgi/rum" => "application/json",
        "/g/collect" => "text/plain;charset=UTF-8",
        _ => "application/x-protobuf"
    };

    //реалистичный mime тип ответа сервера под путь эндпоинта вместо подозрительного octet-stream
    public static string GetStreamResponseContentType(string? path) => path switch
    {
        "/api/v1/stream" => "video/iso.segment",
        "/api/v1/sync" => "application/grpc-web+proto",
        "/v1/telemetry" or "/v2/event" => "application/x-protobuf",
        "/cdn-cgi/rum" => "application/json; charset=utf-8",
        "/g/collect" => "text/plain; charset=utf-8",
        _ => "application/x-protobuf"
    };

    //органичный целевой хост для маскировочного проксирования под профиль браузера
    public static string GetDefaultTargetHost(TlsBrowserProfile profile) => profile switch
    {
        TlsBrowserProfile.SafariIos or TlsBrowserProfile.SafariMac => "www.apple.com",
        TlsBrowserProfile.Yandex => "ya.ru",
        TlsBrowserProfile.Edge => "www.microsoft.com",
        TlsBrowserProfile.Firefox => "www.cloudflare.com",
        TlsBrowserProfile.Opera => "www.microsoft.com",
        TlsBrowserProfile.Chrome or TlsBrowserProfile.ChromePostQuantum => "www.microsoft.com",
        _ => "www.microsoft.com"
    };
}
