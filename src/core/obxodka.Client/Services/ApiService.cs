namespace obxodka.Services;

//сервис запросов к api с защитой от блокировок и обходом dpi
public sealed class ApiService(HttpClient client)
{
    //событие сброса авторизации при 401
    public static event Action? OnUnauthorized;

    //клиент с дефолтным обработчиком
    private static readonly Lazy<HttpClient> s_fallbackNative = new(() => new HttpClient(new HttpClientHandler())
    {
        Timeout = TimeSpan.FromSeconds(8),
        DefaultRequestVersion = new Version(1, 1)
    });

    //клиент tls 1.2 с проверкой сертификата
    private static readonly Lazy<HttpClient> s_fallbackTls12 = new(() => new HttpClient(
        OperatingSystem.IsAndroid()
            ? new HttpClientHandler { UseProxy = false }
            : new SocketsHttpHandler
            {
                SslOptions = SslOptionsFactory.Create(),
                UseProxy = false
            })
    {
        Timeout = TimeSpan.FromSeconds(8),
        DefaultRequestVersion = new Version(1, 1)
    });

    //клиент http 2.0
    private static readonly Lazy<HttpClient> s_fallbackHttp2 = new(() => new HttpClient(
        OperatingSystem.IsAndroid()
            ? new HttpClientHandler { UseProxy = false }
            : new SocketsHttpHandler
            {
                SslOptions = SslOptionsFactory.Create(),
                UseProxy = false
            })
    {
        Timeout = TimeSpan.FromSeconds(8),
        DefaultRequestVersion = new Version(2, 0)
    });

    //кэш адресов doh резолвера
    private static readonly ConcurrentDictionary<string, IPAddress> s_dohCache = new(StringComparer.OrdinalIgnoreCase);

    //клиент для doh запросов
    private static readonly Lazy<HttpClient> s_dohClient = new(() => new HttpClient(
        OperatingSystem.IsAndroid()
            ? new HttpClientHandler { UseProxy = false }
            : new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(2),
                SslOptions = SslOptionsFactory.CreatePublicDoh()
            })
    {
        Timeout = TimeSpan.FromSeconds(2)
    });

    //проверка не является ли ip локальным или заглушкой
    private static bool IsBogonOrSinkholeIpv4(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return true;
        }

        Span<byte> b = stackalloc byte[4];
        _ = ip.TryWriteBytes(b, out _);

        return b[0] is 0 or 10 or 127 ||
               (b[0] == 169 && b[1] == 254) ||
               (b[0] == 172 && b[1] is >= 16 and <= 31) ||
               (b[0] == 192 && b[1] == 168) ||
               (b[0] == 100 && b[1] is >= 64 and <= 127);
    }

    //защищенный резолвинг домена через doh против спуфинга
    public static async ValueTask<IPAddress> ResolveHostAntiPoisonAsync(string host, CancellationToken ct = default)
    {
        if (IPAddress.TryParse(host, out var literalIp))
        {
            return literalIp;
        }

        if (s_dohCache.TryGetValue(host, out var cachedIp))
        {
            return cachedIp;
        }

        try
        {
            var sysAddrs = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            foreach (var addr in sysAddrs)
            {
                if (addr.AddressFamily == AddressFamily.InterNetwork && !IsBogonOrSinkholeIpv4(addr))
                {
                    s_dohCache[host] = addr;
                    return addr;
                }
            }
        }
        catch { }

        var dohResolvers = DnsDefaults.DohResolvers;
        var dohEndpoints = new string[dohResolvers.Length];
        for (var i = 0; i < dohResolvers.Length; i++)
        {
            dohEndpoints[i] = DnsDefaults.BuildDohQueryUrl(dohResolvers[i], host);
        }

        foreach (var dohUrl in dohEndpoints)
        {
            try
            {
                using var dohCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                dohCts.CancelAfter(TimeSpan.FromMilliseconds(1800));

                using var req = new HttpRequestMessage(HttpMethod.Get, dohUrl);
                req.Headers.Accept.ParseAdd("application/dns-json");

                using var resp = await s_dohClient.Value.SendAsync(req, dohCts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    continue;
                }

                var json = await resp.Content.ReadAsStringAsync(dohCts.Token).ConfigureAwait(false);
                var searchIdx = 0;
                while (true)
                {
                    var dataIdx = json.IndexOf("\"data\"", searchIdx, StringComparison.OrdinalIgnoreCase);
                    if (dataIdx < 0)
                    {
                        break;
                    }

                    var colonIdx = json.IndexOf(':', dataIdx + 6);
                    var quoteStart = colonIdx >= 0 ? json.IndexOf('"', colonIdx + 1) : -1;
                    var quoteEnd = quoteStart >= 0 ? json.IndexOf('"', quoteStart + 1) : -1;
                    if (quoteStart < 0 || quoteEnd <= quoteStart)
                    {
                        break;
                    }

                    var candidate = json.Substring(quoteStart + 1, quoteEnd - quoteStart - 1).Trim();
                    searchIdx = quoteEnd + 1;

                    if (IPAddress.TryParse(candidate, out var dohIp) && !IsBogonOrSinkholeIpv4(dohIp))
                    {
                        s_dohCache[host] = dohIp;
                        return dohIp;
                    }
                }
            }
            catch { }
        }

        if (host.Contains("obxodka", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("octocore", StringComparison.OrdinalIgnoreCase))
        {
            var fallbackIp = IPAddress.Parse(AppConfig.DirectServerIp);
            s_dohCache[host] = fallbackIp;
            return fallbackIp;
        }

        throw new SocketException((int)SocketError.HostNotFound);
    }

    //резервный клиент с обходом dpi через сырой поток
    private static readonly Lazy<HttpClient> s_fallbackDirectDpi = new(() =>
    {
        if (OperatingSystem.IsAndroid())
        {
            return new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                Timeout = TimeSpan.FromSeconds(8),
                DefaultRequestVersion = new Version(1, 1)
            };
        }

        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            SslOptions = SslOptionsFactory.Create(targetHost: AppSecrets.GetRandomSni()),
            ConnectCallback = async (context, cToken) =>
            {
                var targetIp = IPAddress.TryParse(context.DnsEndPoint.Host, out var parsedHostIp)
                    ? parsedHostIp
                    : IPAddress.Parse(AppConfig.DirectServerIp);

                int[] fallbackPorts = [443, 8443, 2053];
                Exception? lastConnectEx = null;

                foreach (var port in fallbackPorts)
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    ObxodkaStreamTransport.OnSocketCreated?.Invoke(socket);
                    try
                    {
                        using var portCts = CancellationTokenSource.CreateLinkedTokenSource(cToken);
                        portCts.CancelAfter(TimeSpan.FromMilliseconds(2500));
                        await socket.ConnectAsync(new IPEndPoint(targetIp, port), portCts.Token).ConfigureAwait(false);
                        var netStream = new NetworkStream(socket, ownsSocket: true);
                        return new DpiBypassStream(netStream, bypassEnabled: true);
                    }
                    catch (Exception ex)
                    {
                        lastConnectEx = ex;
                        socket.Dispose();
                    }
                }

                throw lastConnectEx ?? new SocketException((int)SocketError.TimedOut);
            }
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8),
            DefaultRequestVersion = new Version(1, 1)
        };
    });

    //добавление сигнатуры hwid и заголовков к запросу
    private static async ValueTask PrepareRequestAsync(HttpRequestMessage request, bool includeAuth = true)
    {
        if (includeAuth)
        {
            var authHeader = await AuthManager.GetAuthHeaderAsync().ConfigureAwait(false);
            if (authHeader is not null)
            {
                request.Headers.Authorization = authHeader;
            }
        }

        var hwid = DeviceHelper.Hwid;
        _ = request.Headers.Remove("X-Device-Hwid");
        _ = request.Headers.TryAddWithoutValidation("X-Device-Hwid", hwid);

        var pathAndQuery = request.RequestUri?.PathAndQuery ?? "/api";
        var sig = AppSecrets.GenerateStealthSignature(pathAndQuery, out var ts);
        _ = request.Headers.Remove("X-Obx-Timestamp");
        _ = request.Headers.Remove("X-Obx-Sig");
        _ = request.Headers.TryAddWithoutValidation("X-Obx-Timestamp", ts);
        _ = request.Headers.TryAddWithoutValidation("X-Obx-Sig", sig);

        _ = request.Headers.Remove("X-Obx-Proto");
        _ = request.Headers.TryAddWithoutValidation("X-Obx-Proto", "7");
    }

    //создание базового http обработчика с проверкой сертификата
    public static HttpMessageHandler CreateDefaultHandler() =>
        OperatingSystem.IsAndroid()
            ? new HttpClientHandler { UseProxy = false }
            : new SocketsHttpHandler
            {
                UseProxy = false,
                SslOptions = SslOptionsFactory.Create(),
                ConnectCallback = async (context, cToken) =>
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    ObxodkaStreamTransport.OnSocketCreated?.Invoke(socket);
                    try
                    {
                        var resolvedIp = await ResolveHostAntiPoisonAsync(context.DnsEndPoint.Host, cToken).ConfigureAwait(false);
                        await socket.ConnectAsync(new IPEndPoint(resolvedIp, context.DnsEndPoint.Port), cToken).ConfigureAwait(false);
                        var netStream = new NetworkStream(socket, ownsSocket: true);
                        return !IPAddress.IsLoopback(resolvedIp)
                            ? new DpiBypassStream(netStream, bypassEnabled: true)
                            : netStream;
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };

    //отправка запроса с перебором зеркал и обходом блокировок
    private async Task<(bool Success, TResponse? Data, string? Error)> SendRequestAsync<TRequest, TResponse>(
        HttpMethod method,
        string url,
        TRequest? body,
        JsonTypeInfo<TRequest>? requestInfo,
        JsonTypeInfo<TResponse>? responseInfo,
        bool includeAuth = true,
        CancellationToken ct = default)
        where TRequest : class
        where TResponse : class
    {
        if (PlatformServices.Connectivity.NetworkAccess == AppNetworkAccess.None)
        {
            return (false, null, "Нет подключения к интернету. Проверьте сеть и повторите попытку.");
        }

        List<string> candidateDomains = [];
        if (Uri.TryCreate(AppConfig.ApiBaseUrl, UriKind.Absolute, out var curUri) && !IPAddress.TryParse(curUri.Host, out _))
        {
            candidateDomains.Add(AppConfig.ApiBaseUrl);
        }

        foreach (var dom in AppConfig.KnownDomainBases)
        {
            if (!candidateDomains.Any(d => d.StartsWith(dom, StringComparison.OrdinalIgnoreCase)))
            {
                candidateDomains.Add(dom);
            }
        }

        HttpClient[] domainClients = [client, s_fallbackNative.Value, s_fallbackTls12.Value, s_fallbackHttp2.Value];
        Exception? lastEx = null;

        foreach (var baseCandidate in candidateDomains)
        {
            var reqUrl = $"{baseCandidate.TrimEnd('/')}/{url.TrimStart('/')}";

            foreach (var currentClient in domainClients)
            {
                try
                {
                    using var request = new HttpRequestMessage(method, reqUrl);
                    if (body is not null && requestInfo is not null)
                    {
                        request.Content = JsonContent.Create(body, requestInfo);
                    }

                    await PrepareRequestAsync(request, includeAuth).ConfigureAwait(false);
                    var response = await currentClient.SendAsync(request, ct).ConfigureAwait(false);

                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        try
                        {
                            PlatformServices.MainThread.BeginInvokeOnMainThread(() => OnUnauthorized?.Invoke());
                        }
                        catch { }
                        return (false, null, "Сессия истекла или устройство было удалено.");
                    }

                    if (response.IsSuccessStatusCode)
                    {
                        AppConfig.ApiBaseUrl = baseCandidate;
                        if (responseInfo is not null)
                        {
                            return (true, await response.Content.ReadFromJsonAsync(responseInfo, ct).ConfigureAwait(false), null);
                        }
                        return (true, null, null);
                    }

                    var rawError = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(rawError))
                    {
                        var trimmed = rawError.Trim();
                        if (trimmed.StartsWith('{'))
                        {
                            try
                            {
                                var errorObj = JsonSerializer.Deserialize(trimmed, AppJsonContext.Default.MessageResponse);
                                if (errorObj is { Message: { Length: > 0 } msg })
                                {
                                    return (false, null, msg);
                                }
                            }
                            catch { }
                        }
                        return (false, null, trimmed);
                    }

                    return (false, null, $"Ошибка сервера {(int)response.StatusCode}: {response.ReasonPhrase}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    var innerInfo = ex.InnerException != null ? $" -> {ex.InnerException.GetType().Name}: {ex.InnerException.Message}" : "";
                    AppLogger.LogWarning($"[API] {method} {reqUrl} direct attempt failed: {ex.Message}{innerInfo}");

                    var isTlsBlock = ex is HttpRequestException && (ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("handshake", StringComparison.OrdinalIgnoreCase) || ex.InnerException is AuthenticationException or SocketException);
                    if (isTlsBlock)
                    {
                        AppLogger.LogWarning($"[API-DPI] ISP/TSPU TLS block detected on {baseCandidate}. Fast-tracking to DPI bypass...");
                        break;
                    }
                }
            }
        }

        //резервная попытка через прямой ip и обход dpi
        string[] directHosts = [AppSecrets.GetRandomSni(), AppConfig.PrimaryDomain, AppConfig.MirrorDomain];
        var trimmedPath = url.TrimStart('/');
        string[] pathVariants = trimmedPath.StartsWith("api/", StringComparison.OrdinalIgnoreCase)
            ? [trimmedPath, $"v1/{trimmedPath}", $"cdn-cgi/{trimmedPath}"]
            : [trimmedPath];

        foreach (var relayIp in AppConfig.DirectRelayIps)
        {
            foreach (var pathVariant in pathVariants)
            {
                foreach (var directHost in directHosts)
                {
                    try
                    {
                        var directUrl = $"https://{relayIp}/{pathVariant}";
                        using var request = new HttpRequestMessage(method, directUrl);
                        request.Headers.Host = directHost;
                        if (body is not null && requestInfo is not null)
                        {
                            request.Content = JsonContent.Create(body, requestInfo);
                        }

                        await PrepareRequestAsync(request, includeAuth).ConfigureAwait(false);
                        var response = await s_fallbackDirectDpi.Value.SendAsync(request, ct).ConfigureAwait(false);

                        if (response.StatusCode == HttpStatusCode.Unauthorized)
                        {
                            try
                            {
                                PlatformServices.MainThread.BeginInvokeOnMainThread(() => OnUnauthorized?.Invoke());
                            }
                            catch { }
                            return (false, null, "Сессия истекла или устройство было удалено.");
                        }

                        if (response.IsSuccessStatusCode)
                        {
                            Debug.WriteLine($"[API-DPI] Request {method} {pathVariant} succeeded via DPI bypass stream (IP {relayIp}, Host {directHost})");
                            if (responseInfo is not null)
                            {
                                return (true, await response.Content.ReadFromJsonAsync(responseInfo, ct).ConfigureAwait(false), null);
                            }
                            return (true, null, null);
                        }

                        var rawError = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(rawError))
                        {
                            var trimmed = rawError.Trim();
                            if (trimmed.StartsWith('{'))
                            {
                                try
                                {
                                    var errorObj = JsonSerializer.Deserialize(trimmed, AppJsonContext.Default.MessageResponse);
                                    if (errorObj is { Message: { Length: > 0 } msg })
                                    {
                                        return (false, null, msg);
                                    }
                                }
                                catch { }
                            }
                            return (false, null, trimmed);
                        }

                        return (false, null, $"Ошибка сервера {(int)response.StatusCode}: {response.ReasonPhrase}");
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                        AppLogger.LogError($"[API DIRECT DPI ERROR] {method} {relayIp} ({directHost}): {ex.Message}", ex);
                    }
                }
            }
        }

        var baseEx = lastEx?.GetBaseException();
        var message = baseEx is not null && baseEx != lastEx ? $"{lastEx!.Message} ({baseEx.Message})" : lastEx?.Message;
        return (false, null, message ?? "Не удалось связаться с сервером");
    }

    //отправка post запроса
    public async Task<(bool Success, string? Error)> PostAsync<TRequest>(
        string url,
        TRequest? body,
        JsonTypeInfo<TRequest>? typeInfo,
        bool includeAuth = true,
        CancellationToken ct = default)
        where TRequest : class
    {
        var (success, _, error) = await SendRequestAsync<TRequest, object>(
            HttpMethod.Post, url, body, typeInfo, null, includeAuth, ct).ConfigureAwait(false);
        return (success, error);
    }

    //отправка post запроса с получением ответа
    public async Task<(bool Success, TResponse? Data, string? Error)> PostWithResponseAsync<TRequest, TResponse>(
        string url,
        TRequest? body,
        JsonTypeInfo<TRequest> requestInfo,
        JsonTypeInfo<TResponse> responseInfo,
        bool includeAuth = true,
        CancellationToken ct = default)
        where TRequest : class
        where TResponse : class =>
        await SendRequestAsync(HttpMethod.Post, url, body, requestInfo, responseInfo, includeAuth, ct).ConfigureAwait(false);

    //генерация ссылки на оплату
    public async Task<(bool Success, string? Url, string? Error)> GeneratePaymentLinkAsync(decimal amount, string currency = "RUB", CancellationToken ct = default)
    {
        var amountStr = amount.ToString(CultureInfo.InvariantCulture);
        var url = $"api/payment/generate?amount={amountStr}&currency={Uri.EscapeDataString(currency)}";
        var (success, data, error) = await SendRequestAsync<object, PaymentLinkResponse>(
            HttpMethod.Get, url, null, null, AppJsonContext.Default.PaymentLinkResponse, includeAuth: true, ct: ct);

        if (success && data is { Url: { Length: > 0 } paymentUrl })
        {
            return (true, paymentUrl, null);
        }

        return (false, null, error ?? "Unknown error generating payment link");
    }

    //получение профиля пользователя
    public async Task<(bool Success, UserProfileResponse? Data, string? Error)> GetProfileAsync(int maxRetries = 2, CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            var result = await SendRequestAsync<object, UserProfileResponse>(
                HttpMethod.Get, "api/Auth/me", null, null, AppJsonContext.Default.UserProfileResponse, includeAuth: true, ct: ct).ConfigureAwait(false);

            if (result.Success || attempt >= maxRetries || PlatformServices.Connectivity.NetworkAccess == AppNetworkAccess.None)
            {
                return result;
            }

            await Task.Delay(attempt * 500, ct).ConfigureAwait(false);
        }

        return (false, null, "Не удалось получить профиль");
    }

    //запрос кода авторизации на почту
    public async Task<(bool Success, string? Error)> RequestCodeAsync(EmailAuthRequest request, CancellationToken ct = default) =>
        await PostAsync("api/Auth/request-code", request, AppJsonContext.Default.EmailAuthRequest, includeAuth: false, ct: ct).ConfigureAwait(false);

    //проверка кода авторизации
    public async Task<(bool Success, LoginResponse? Data, string? Error)> VerifyCodeAsync(EmailVerifyRequest request, CancellationToken ct = default) =>
        await PostWithResponseAsync(
            "api/Auth/verify-code", request, AppJsonContext.Default.EmailVerifyRequest, AppJsonContext.Default.LoginResponse, includeAuth: false, ct: ct).ConfigureAwait(false);

    //список привязанных устройств
    public async Task<(bool Success, List<DeviceItem>? Devices, string? Error)> GetDevicesAsync(CancellationToken ct = default) =>
        await SendRequestAsync<object, List<DeviceItem>>(
            HttpMethod.Get, "api/Auth/devices", null, null, AppJsonContext.Default.ListDeviceItem, includeAuth: true, ct: ct).ConfigureAwait(false);

    //удаление устройства
    public async Task<(bool Success, string? Error)> RemoveDeviceAsync(string hwid, CancellationToken ct = default)
    {
        var url = $"api/Auth/devices/{Uri.EscapeDataString(hwid)}";
        var (success, _, error) = await SendRequestAsync<object, object>(
            HttpMethod.Delete, url, null, null, null, includeAuth: true, ct: ct).ConfigureAwait(false);
        return (success, error);
    }

    //удаление аккаунта пользователя
    public async Task<(bool Success, string? Error)> DeleteAccountAsync(CancellationToken ct = default)
    {
        var (success, _, error) = await SendRequestAsync<object, object>(
            HttpMethod.Delete, "api/Auth/delete-user", null, null, null, includeAuth: true, ct: ct).ConfigureAwait(false);
        return (success, error);
    }

    //остановка сессии vpn на сервере
    public async Task StopVpnOnServerAsync(CancellationToken ct = default) =>
        _ = await PostAsync<object>("api/Vpn/stop", null, null, includeAuth: true, ct: ct).ConfigureAwait(false);

    //расшифровка адреса сервера
    public static string DecodeObfuscatedEndpoint(string endpoint) =>
        AppSecrets.DecodeObfuscatedEndpoint(endpoint);

    //нормализация и расшифровка списка серверов
    private static List<VpnServerDto> NormalizeServers(List<VpnServerDto> servers)
    {
        for (var i = 0; i < servers.Count; i++)
        {
            var s = servers[i];
            var decodedIp = DecodeObfuscatedEndpoint(s.Ip);
            if (!string.Equals(decodedIp, s.Ip, StringComparison.Ordinal))
            {
                servers[i] = s with { Ip = decodedIp };
            }
        }
        return servers;
    }

    //получение списка серверов с сервера или репозитория
    public async Task<(bool Success, List<VpnServerDto>? Servers, string? Error)> GetServersAsync(int maxRetries = 3, CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            var result = await SendRequestAsync<object, List<VpnServerDto>>(
                HttpMethod.Get, "api/Vpn/servers", null, null, AppJsonContext.Default.ListVpnServerDto, includeAuth: true, ct: ct).ConfigureAwait(false);

            if (result.Data is { Count: > 0 } serverList)
            {
                _ = NormalizeServers(serverList);
            }

            if (result.Success && result.Data is { Count: > 0 })
            {
                return result;
            }

            if (attempt >= maxRetries || PlatformServices.Connectivity.NetworkAccess == AppNetworkAccess.None)
            {
                break;
            }

            await Task.Delay(attempt * 600, ct).ConfigureAwait(false);
        }

        try
        {
            using var rawClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3.5) };
            foreach (var discoveryUrl in AppConfig.RemoteServersDiscoveryUrls)
            {
                try
                {
                    using var rawCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    rawCts.CancelAfter(TimeSpan.FromSeconds(3.5));
                    var rawJson = await rawClient.GetStringAsync(discoveryUrl, rawCts.Token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(rawJson))
                    {
                        var discovered = JsonSerializer.Deserialize(rawJson, AppJsonContext.Default.ListVpnServerDto);
                        if (discovered is { Count: > 0 })
                        {
                            return (true, NormalizeServers(discovered), null);
                        }
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[API-DISCOVERY] Remote discovery attempt failed: {ex.Message}");
        }

        AppLogger.LogWarning($"[API-DISCOVERY] Remote servers discovery failed. Falling back to {AppConfig.BootstrapServers.Count} built-in bootstrap servers.");
        return (true, NormalizeServers([.. AppConfig.BootstrapServers]), null);
    }

    //получение хэша сертификата для ssl pinning
    public async Task<(bool Success, CertHashResponse? Data, string? Error)> GetCertHashAsync(CancellationToken ct = default) =>
        await SendRequestAsync<object, CertHashResponse>(
            HttpMethod.Get, "api/Vpn/cert-hash", null, null, AppJsonContext.Default.CertHashResponse, includeAuth: false, ct: ct).ConfigureAwait(false);

    //проверка покупки google play
    public async Task<(bool Success, string? Error)> VerifyGooglePurchaseAsync(string productId, string purchaseToken, string? orderId, CancellationToken ct = default)
    {
        var request = new GooglePurchaseVerifyRequest(productId, purchaseToken, orderId);
        return await PostAsync("api/Payment/google-verify", request, AppJsonContext.Default.GooglePurchaseVerifyRequest, includeAuth: true, ct: ct).ConfigureAwait(false);
    }

    //получение реферального кода
    public async Task<(bool Success, ReferralCodeResponse? Data, string? Error)> GetMyReferralCodeAsync(CancellationToken ct = default) =>
        await SendRequestAsync<object, ReferralCodeResponse>(
            HttpMethod.Get, "api/Referral/my-code", null, null, AppJsonContext.Default.ReferralCodeResponse, includeAuth: true, ct: ct).ConfigureAwait(false);

    //активация реферального кода
    public async Task<(bool Success, MessageResponse? Data, string? Error)> ActivateReferralCodeAsync(string code, CancellationToken ct = default) =>
        await PostWithResponseAsync(
            "api/Referral/activate", new ActivateReferralRequest(code), AppJsonContext.Default.ActivateReferralRequest, AppJsonContext.Default.MessageResponse, includeAuth: true, ct: ct).ConfigureAwait(false);

    //отправка отчета телеметрии на сервер
    public async Task<bool> PostTelemetryAsync(TelemetryDto telemetry, CancellationToken ct = default)
    {
        var (success, _) = await PostAsync(
            "api/Vpn/telemetry",
            telemetry,
            AppJsonContext.Default.TelemetryDto,
            includeAuth: false,
            ct: ct).ConfigureAwait(false);
        return success;
    }
}
