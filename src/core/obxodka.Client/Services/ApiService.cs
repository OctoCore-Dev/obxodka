namespace obxodka.Services;

//сервис запросов к api с защитой от блокировок и обходом dpi
public sealed class ApiService(HttpClient client)
{
    //клиент с дефолтным обработчиком и строгим ssl pinning
    private static readonly Lazy<HttpClient> s_fallbackNative = new(() => new HttpClient(CreateApiSocketsHandler())
    {
        Timeout = TimeSpan.FromSeconds(8),
        DefaultRequestVersion = new Version(1, 1)
    });

    //клиент http 2.0
    private static readonly Lazy<HttpClient> s_fallbackHttp2 = new(() => new HttpClient(CreateApiSocketsHandler())
    {
        Timeout = TimeSpan.FromSeconds(8),
        DefaultRequestVersion = new Version(2, 0)
    });

    //клиент для загрузки резервного списка серверов (discovery)
    private static readonly Lazy<HttpClient> s_discoveryClient = new(() => new HttpClient(CreateDefaultHandler())
    {
        Timeout = TimeSpan.FromSeconds(3.5)
    });

    //кэш списков адресов doh резолвера с временем жизни
    private static readonly ConcurrentDictionary<string, (IPAddress[] Addresses, long ExpiresAtTicks)> s_dohCache =
        new(StringComparer.OrdinalIgnoreCase);

    //клиент для doh запросов с привязкой к физическому адаптеру мимо туннеля
    private static readonly Lazy<HttpClient> s_dohClient = new(() => new HttpClient(
        new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            SslOptions = SslOptionsFactory.CreatePublicDoh(),
            ConnectCallback = async (context, cToken) =>
            {
                var host = context.DnsEndPoint.Host;
                IPAddress? targetIp = null;
                if (IPAddress.TryParse(host, out var parsedIp))
                {
                    targetIp = parsedIp;
                }
                else if (host.Equals("cloudflare-dns.com", StringComparison.OrdinalIgnoreCase))
                {
                    targetIp = IPAddress.Parse("1.1.1.1");
                }
                else if (host.Equals("dns.adguard.com", StringComparison.OrdinalIgnoreCase))
                {
                    targetIp = IPAddress.Parse("94.140.14.14");
                }
                else
                {
                    var resolved = await Dns.GetHostAddressesAsync(host, cToken).ConfigureAwait(false);
                    foreach (var a in resolved)
                    {
                        if (a.AddressFamily != AddressFamily.InterNetwork || IsBogonOrSinkholeIpv4(a))
                        {
                            continue;
                        }

                        targetIp = a;
                        break;
                    }
                }

                targetIp ??= IPAddress.Parse("1.1.1.1");
                var socket = new Socket(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };
                OctopusEngine.Current.ProtectSocket(socket);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(targetIp, context.DnsEndPoint.Port), cToken).ConfigureAwait(false);
                    return new NetworkStream(socket, true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        })
    {
        Timeout = TimeSpan.FromSeconds(2)
    });

    //резервный клиент с обходом dpi через сырой поток
    private static readonly Lazy<HttpClient> s_fallbackDirectDpi = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            //для прямого подключения к ip включаем строгий sha-256 spki пиннинг нашего корневого сертификата
            SslOptions = SslOptionsFactory.Create(dynamicPinningHash: AppSecrets.DirectServerSpkiPin),
            ConnectCallback = async (context, cToken) =>
            {
                var targetIp = IPAddress.TryParse(context.DnsEndPoint.Host, out var parsedHostIp)
                    ? parsedHostIp
                    : IPAddress.Parse(AppConfig.DirectServerIp);

                int[] fallbackPorts = [443, 8443, 2053];
                Exception? lastConnectEx = null;

                foreach (var port in fallbackPorts)
                {
                    var socket = new Socket(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true
                    };
                    OctopusEngine.Current.ProtectSocket(socket);
                    try
                    {
                        using var portCts = CancellationTokenSource.CreateLinkedTokenSource(cToken);
                        portCts.CancelAfter(TimeSpan.FromMilliseconds(2500));
                        await socket.ConnectAsync(new IPEndPoint(targetIp, port), portCts.Token).ConfigureAwait(false);
                        var netStream = new NetworkStream(socket, true);
                        return new DpiBypassStream(netStream, bypassEnabled: true);
                    }
                    catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or OperationCanceledException
                                                   or ObjectDisposedException or AuthenticationException)
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

    //флаг одноразового уведомления о протухшей сессии до нового входа
    private static int s_unauthorizedNotified;

    //общий экземпляр для фоновых сервисов без di (телеметрия и инспектор)
    internal static ApiService Shared { get; } = new(s_fallbackNative.Value);

    //признак уже отправленного сигнала истечения текущей сессии
    public static bool IsUnauthorizedPending => Volatile.Read(ref s_unauthorizedNotified) != 0;

    //событие сброса авторизации при 401
    public static event Action? OnUnauthorized;

    //сброс защелки 401 при сохранении новой авторизованной сессии
    public static void ResetUnauthorizedState() => Volatile.Write(ref s_unauthorizedNotified, 0);

    //создание базового обработчика с шифрованным doh и обходом dpi без привязки к пину туннеля
    private static SocketsHttpHandler CreateApiSocketsHandler() => new()
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(45),
        SslOptions = SslOptionsFactory.Create(dynamicPinningHash: ""),
        ConnectCallback = async (context, cToken) =>
        {
            var host = context.DnsEndPoint.Host;
            var port = context.DnsEndPoint.Port;
            var candidates = SniEngine.IsInternalBrandHost(host)
                ? await CleanEdgeScanner.ResolveCleanShieldCandidatesAsync(host, port, cToken).ConfigureAwait(false)
                : [await ResolveHostAntiPoisonAsync(host, cToken).ConfigureAwait(false)];

            Exception? lastEx = null;
            for (var i = 0; i < candidates.Length; i++)
            {
                var resolvedIp = candidates[i];
                var socket = new Socket(resolvedIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };
                OctopusEngine.Current.ProtectSocket(socket);
                try
                {
                    if (candidates.Length > 1 && i + 1 < candidates.Length)
                    {
                        using var perIpCts = CancellationTokenSource.CreateLinkedTokenSource(cToken);
                        perIpCts.CancelAfter(TimeSpan.FromMilliseconds(1600));
                        await socket.ConnectAsync(new IPEndPoint(resolvedIp, port), perIpCts.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await socket.ConnectAsync(new IPEndPoint(resolvedIp, port), cToken).ConfigureAwait(false);
                    }

                    var netStream = new NetworkStream(socket, true);
                    return !IPAddress.IsLoopback(resolvedIp)
                        ? DpiBypassStream.CreateForProfile(netStream, TlsBrowserProfile.Chrome, true, true)
                        : netStream;
                }
                catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
                {
                    lastEx = ex;
                    EdgeEndpointHealth.MarkUnhealthy(resolvedIp.ToString(), 60);
                    socket.Dispose();
                    if (cToken.IsCancellationRequested)
                    {
                        throw;
                    }
                }
            }

            throw lastEx ?? new SocketException((int)SocketError.HostUnreachable);
        }
    };

    //сброс кэша резолвинга при смене сети или недоступности узла
    public static void InvalidateHostCache(string? host = null)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            s_dohCache.Clear();
            CleanEdgeScanner.InvalidateCache();
            return;
        }

        _ = s_dohCache.TryRemove(host, out _);
    }

    //проверка не является ли ip локальным или заглушкой
    private static bool IsBogonOrSinkholeIpv4(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return true;
        }

        Span<byte> b = stackalloc byte[4];
        _ = ip.TryWriteBytes(b, out _);

        return b[0] switch
        {
            0 or 10 or 127 => true,
            169 => b[1] == 254,
            172 => b[1] is >= 16 and <= 31,
            192 => b[1] == 168,
            100 => b[1] is >= 64 and <= 127,
            _ => false
        };
    }

    //защищенный резолвинг всех a записей домена с приоритетом doh против спуфинга провайдера
    public static async ValueTask<IPAddress[]> ResolveAllHostsAntiPoisonAsync(string host, CancellationToken ct = default)
    {
        if (IPAddress.TryParse(host, out var literalIp))
        {
            return [literalIp];
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return [IPAddress.Loopback];
        }

        var nowTicks = Stopwatch.GetTimestamp();
        if (s_dohCache.TryGetValue(host, out var cached) && cached.Addresses.Length > 0 && nowTicks < cached.ExpiresAtTicks)
        {
            return cached.Addresses;
        }

        //сначала опрашиваем шифрованный doh чтобы исключить публичные заглушки провайдера на udp 53
        var dohResolvers = DnsDefaults.DohResolvers;
        List<IPAddress> results = [with(4)];

        foreach (var t in dohResolvers)
        {
            var dohUrl = DnsDefaults.BuildDohQueryUrl(t, host);
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

                    if (IPAddress.TryParse(candidate, out var dohIp) &&
                        !IsBogonOrSinkholeIpv4(dohIp) &&
                        !results.Contains(dohIp))
                    {
                        results.Add(dohIp);
                    }
                }

                if (results.Count <= 0)
                {
                    continue;
                }

                var resolvedArr = results.ToArray();
                s_dohCache[host] = (resolvedArr, Stopwatch.GetTimestamp() + Stopwatch.Frequency * 600);
                return resolvedArr;
            }
            catch (Exception ex) when (ex is HttpRequestException or SocketException or OperationCanceledException or TimeoutException
                                           or JsonException or InvalidOperationException or FormatException)
            {
                //подавление сетевой ошибки резолвинга doh
            }
        }

        //резервный системный днс только если все doh серверы заблокированы сетью
        try
        {
            var sysAddrs = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            foreach (var addr in sysAddrs.Where(addr =>
                         addr.AddressFamily == AddressFamily.InterNetwork && !IsBogonOrSinkholeIpv4(addr) && !results.Contains(addr)))
            {
                results.Add(addr);
            }

            if (results.Count > 0)
            {
                var sysArr = results.ToArray();
                s_dohCache[host] = (sysArr, Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60);
                return sysArr;
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            //подавление сетевой ошибки системного днс
        }

        ct.ThrowIfCancellationRequested();

        if (!host.Contains("obxodka", StringComparison.OrdinalIgnoreCase) &&
            !host.Contains("octocore", StringComparison.OrdinalIgnoreCase))
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        var fallbackArr = CleanEdgeScanner.GenerateProbeBatch(4);
        s_dohCache[host] = (fallbackArr, Stopwatch.GetTimestamp() + Stopwatch.Frequency * 30);
        return fallbackArr;
    }

    //защищенный резолвинг основного адреса домена через doh
    public static async ValueTask<IPAddress> ResolveHostAntiPoisonAsync(string host, CancellationToken ct = default)
    {
        var all = await ResolveAllHostsAntiPoisonAsync(host, ct).ConfigureAwait(false);
        return all[0];
    }

    //добавление сигнатуры hwid и заголовков к запросу
    private static async ValueTask PrepareRequestAsync(HttpRequestMessage? request, bool includeAuth = true)
    {
        if (request?.Headers is null)
        {
            return;
        }

        if (includeAuth)
        {
            var authHeader = await AuthManager.GetAuthHeaderAsync().ConfigureAwait(false);
            if (authHeader is not null)
            {
                request.Headers.Authorization = authHeader;
            }
        }

        var hwid = DeviceHelper.Hwid;
        if (!string.IsNullOrEmpty(hwid))
        {
            _ = request.Headers.Remove("X-Device-Hwid");
            _ = request.Headers.TryAddWithoutValidation("X-Device-Hwid", hwid);
        }

        if (!string.IsNullOrEmpty(AppConfig.TelemetryAuthToken))
        {
            _ = request.Headers.Remove("X-Telemetry-Token");
            _ = request.Headers.TryAddWithoutValidation("X-Telemetry-Token", AppConfig.TelemetryAuthToken);
        }

        _ = request.Headers.Remove("X-Obx-Proto");
        _ = request.Headers.TryAddWithoutValidation("X-Obx-Proto", "7");
    }

    //создание базового http обработчика с проверкой сертификата
    public static HttpMessageHandler CreateDefaultHandler() => CreateApiSocketsHandler();

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
        if (PlatformServices.Connectivity != null && PlatformServices.Connectivity.NetworkAccess == AppNetworkAccess.None)
        {
            return (false, null, "Нет подключения к интернету. Проверьте сеть и повторите попытку.");
        }

        List<string> candidateDomains = [];
        if (Uri.TryCreate(AppConfig.ApiBaseUrl, UriKind.Absolute, out var curUri) && !IPAddress.TryParse(curUri.Host, out _))
        {
            candidateDomains.Add(AppConfig.ApiBaseUrl);
        }

        foreach (var dom in AppConfig.KnownDomainBases.Where(dom =>
                     !candidateDomains.Any(d => d.StartsWith(dom, StringComparison.OrdinalIgnoreCase))))
        {
            candidateDomains.Add(dom);
        }

        HttpClient[] domainClients = [client, s_fallbackNative.Value, s_fallbackHttp2.Value];
        Exception? lastEx = null;
        string? lastHttpError = null;

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
                    using var response = await currentClient.SendAsync(request, ct).ConfigureAwait(false);

                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        if (includeAuth && request.Headers.Authorization is not null)
                        {
                            NotifyUnauthorized();
                        }

                        var authErr = !includeAuth ? await ReadHttpErrorAsync(response, ct).ConfigureAwait(false) : null;
                        return (false, null, string.IsNullOrWhiteSpace(authErr)
                            ? "Сессия истекла или устройство было удалено."
                            : authErr);
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

                    var errorMsg = await ReadHttpErrorAsync(response, ct).ConfigureAwait(false);
                    //блокировки waf/cloudflare (403) и транзиентные сбои 5xx переключаются на следующее зеркало и прямой dpi канал
                    if (response.StatusCode != HttpStatusCode.Forbidden && (int)response.StatusCode < 500)
                    {
                        return (false, null, errorMsg);
                    }

                    lastHttpError = errorMsg;
                    AppLogger.LogWarning($"[API] {method} {reqUrl} returned {(int)response.StatusCode}. Trying fallback...");
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or TimeoutException
                                               or TaskCanceledException or AuthenticationException or JsonException
                                               or InvalidOperationException)
                {
                    lastEx = ex;
                    var innerInfo = ex.InnerException != null ? $" -> {ex.InnerException.GetType().Name}: {ex.InnerException.Message}" : "";
                    AppLogger.LogWarning($"[API] {method} {reqUrl} direct attempt failed: {ex.Message}{innerInfo}");

                    var isTlsBlock = ex is TaskCanceledException or TimeoutException ||
                                     ex is HttpRequestException && (ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
                                                                    ex.Message.Contains("handshake", StringComparison.OrdinalIgnoreCase) ||
                                                                    ex.Message.Contains("reset", StringComparison.OrdinalIgnoreCase) ||
                                                                    ex.InnerException is AuthenticationException or SocketException);
                    if (!isTlsBlock)
                    {
                        continue;
                    }

                    AppLogger.LogWarning(
                        $"[API-DPI] ISP/TSPU block or timeout detected on {baseCandidate}. Fast-tracking to next mirror / DPI bypass...");
                    break;
                }
            }
        }

        //резервная попытка через прямой ip и обход dpi с реальными доменами щита
        string[] directHosts = [AppConfig.PrimaryDomain, AppConfig.MirrorDomain];
        var trimmedPath = url.TrimStart('/');

        foreach (var relayIp in AppConfig.DirectRelayIps)
        {
            foreach (var directHost in directHosts)
            {
                if (string.IsNullOrWhiteSpace(directHost))
                {
                    continue;
                }

                try
                {
                    var directUrl = $"https://{relayIp}/{trimmedPath}";
                    using var request = new HttpRequestMessage(method, directUrl);
                    request.Headers.Host = directHost;
                    if (body is not null && requestInfo is not null)
                    {
                        request.Content = JsonContent.Create(body, requestInfo);
                    }

                    await PrepareRequestAsync(request, includeAuth).ConfigureAwait(false);
                    using var response = await s_fallbackDirectDpi.Value.SendAsync(request, ct).ConfigureAwait(false);

                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        if (includeAuth && request.Headers.Authorization is not null)
                        {
                            NotifyUnauthorized();
                        }

                        var authErr = !includeAuth ? await ReadHttpErrorAsync(response, ct).ConfigureAwait(false) : null;
                        return (false, null, string.IsNullOrWhiteSpace(authErr)
                            ? "Сессия истекла или устройство было удалено."
                            : authErr);
                    }

                    if (response.IsSuccessStatusCode)
                    {
                        AppLogger.LogDebug(
                            $"[API-DPI] Request {method} {trimmedPath} succeeded via DPI bypass stream (IP {relayIp}, Host {directHost})");
                        if (responseInfo is not null)
                        {
                            return (true, await response.Content.ReadFromJsonAsync(responseInfo, ct).ConfigureAwait(false), null);
                        }

                        return (true, null, null);
                    }

                    var errorMsg = await ReadHttpErrorAsync(response, ct).ConfigureAwait(false);
                    if (response.StatusCode != HttpStatusCode.Forbidden && (int)response.StatusCode < 500)
                    {
                        return (false, null, errorMsg);
                    }

                    lastHttpError = errorMsg;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or TimeoutException
                                               or TaskCanceledException or AuthenticationException or JsonException
                                               or InvalidOperationException)
                {
                    lastEx = ex;
                    AppLogger.LogError($"[API DIRECT DPI ERROR] {method} {relayIp} ({directHost}): {ex.Message}", ex);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(lastHttpError))
        {
            return (false, null, lastHttpError);
        }

        var baseEx = lastEx?.GetBaseException();
        var message = baseEx is not null && baseEx != lastEx ? $"{lastEx!.Message} ({baseEx.Message})" : lastEx?.Message;
        return (false, null, message ?? "Не удалось связаться с сервером");
    }

    private static void NotifyUnauthorized()
    {
        if (Interlocked.CompareExchange(ref s_unauthorizedNotified, 1, 0) != 0)
        {
            return;
        }

        try
        {
            PlatformServices.MainThread?.BeginInvokeOnMainThread(() => OnUnauthorized?.Invoke());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            //подавление ошибки вызова события деавторизации в ui потоке
        }
    }

    private static async ValueTask<string> ReadHttpErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var rawError = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(rawError))
        {
            var trimmed = rawError.Trim();
            if (!trimmed.StartsWith('{'))
            {
                return trimmed;
            }

            try
            {
                var errorObj = JsonSerializer.Deserialize(trimmed, AppJsonContext.Default.MessageResponse);
                if (errorObj is { Message: { Length: > 0 } msg })
                {
                    return msg;
                }
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                //подавление ошибки десериализации ответа ошибки api
            }

            return trimmed;
        }

        return $"Ошибка сервера {(int)response.StatusCode}: {response.ReasonPhrase}";
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

    //генерация ссылки на оплату с выбором валюты и способа оплаты
    public async Task<(bool Success, string? Url, string? Error)> GeneratePaymentLinkAsync(
        decimal amount,
        string currency = "RUB",
        string? paymentMethod = null,
        CancellationToken ct = default)
    {
        var amountStr = amount.ToString(CultureInfo.InvariantCulture);
        var url = $"api/payment/generate?amount={amountStr}&currency={Uri.EscapeDataString(currency)}";
        if (!string.IsNullOrWhiteSpace(paymentMethod))
        {
            url += $"&method={Uri.EscapeDataString(paymentMethod)}";
        }

        var (success, data, error) = await SendRequestAsync<object, PaymentLinkResponse>(
            HttpMethod.Get, url, null, null, AppJsonContext.Default.PaymentLinkResponse, true, ct);

        if (success && data is { Url: { Length: > 0 } paymentUrl })
        {
            return (true, paymentUrl, null);
        }

        return (false, null, error ?? "Unknown error generating payment link");
    }

    //сверка незавершенных платежей пользователя с платежным шлюзом
    public async Task<(bool Success, PaymentSyncResponse? Data, string? Error)> SyncPendingPaymentsAsync(CancellationToken ct = default) =>
        await SendRequestAsync<object, PaymentSyncResponse>(
                HttpMethod.Get, "api/payment/sync-pending", null, null, AppJsonContext.Default.PaymentSyncResponse, true, ct)
            .ConfigureAwait(false);

    //получение профиля пользователя
    public async Task<(bool Success, UserProfileResponse? Data, string? Error)> GetProfileAsync(int maxRetries = 2,
        CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            var result = await SendRequestAsync<object, UserProfileResponse>(
                HttpMethod.Get, "api/Auth/me", null, null, AppJsonContext.Default.UserProfileResponse, true, ct).ConfigureAwait(false);

            if (PlatformServices.Connectivity != null && (result.Success || attempt >= maxRetries ||
                                                          PlatformServices.Connectivity.NetworkAccess == AppNetworkAccess.None))
            {
                return result;
            }

            await Task.Delay(attempt * 500, ct).ConfigureAwait(false);
        }

        return (false, null, "Не удалось получить профиль");
    }

    //запрос кода авторизации на почту
    public async Task<(bool Success, string? Error)> RequestCodeAsync(EmailAuthRequest request, CancellationToken ct = default) =>
        await PostAsync("api/Auth/request-code", request, AppJsonContext.Default.EmailAuthRequest, false, ct).ConfigureAwait(false);

    //проверка кода авторизации
    public async Task<(bool Success, LoginResponse? Data, string? Error)> VerifyCodeAsync(EmailVerifyRequest request,
        CancellationToken ct = default) =>
        await PostWithResponseAsync(
                "api/Auth/verify-code", request, AppJsonContext.Default.EmailVerifyRequest, AppJsonContext.Default.LoginResponse, false, ct)
            .ConfigureAwait(false);

    //список привязанных устройств
    public async Task<(bool Success, List<DeviceItem>? Devices, string? Error)> GetDevicesAsync(CancellationToken ct = default) =>
        await SendRequestAsync<object, List<DeviceItem>>(
            HttpMethod.Get, "api/Auth/devices", null, null, AppJsonContext.Default.ListDeviceItem, true, ct).ConfigureAwait(false);

    //удаление устройства
    public async Task<(bool Success, string? Error)> RemoveDeviceAsync(string hwid, CancellationToken ct = default)
    {
        var url = $"api/Auth/devices/{Uri.EscapeDataString(hwid)}";
        var (success, _, error) = await SendRequestAsync<object, object>(
            HttpMethod.Delete, url, null, null, null, true, ct).ConfigureAwait(false);
        return (success, error);
    }

    //удаление аккаунта пользователя
    public async Task<(bool Success, string? Error)> DeleteAccountAsync(CancellationToken ct = default)
    {
        var (success, _, error) = await SendRequestAsync<object, object>(
            HttpMethod.Delete, "api/Auth/delete-user", null, null, null, true, ct).ConfigureAwait(false);
        return (success, error);
    }

    //остановка сессии vpn на сервере
    public async Task StopVpnOnServerAsync(CancellationToken ct = default) =>
        _ = await PostAsync<object>("api/Vpn/stop", null, null, true, ct).ConfigureAwait(false);

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
            string[]? decodedMirrors = null;
            if (s.Mirrors is { Length: > 0 })
            {
                for (var m = 0; m < s.Mirrors.Length; m++)
                {
                    var dm = DecodeObfuscatedEndpoint(s.Mirrors[m]);
                    if (!string.Equals(dm, s.Mirrors[m], StringComparison.Ordinal))
                    {
                        decodedMirrors ??= (string[])s.Mirrors.Clone();
                        decodedMirrors[m] = dm;
                    }
                }
            }

            if (!string.Equals(decodedIp, s.Ip, StringComparison.Ordinal) || decodedMirrors is not null)
            {
                servers[i] = s with
                {
                    Ip = decodedIp,
                    Mirrors = decodedMirrors ?? s.Mirrors
                };
            }
        }

        return servers;
    }

    //получение списка серверов с сервера или репозитория
    public async Task<(bool Success, List<VpnServerDto>? Servers, string? Error)> GetServersAsync(int maxRetries = 3,
        CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            var result = await SendRequestAsync<object, List<VpnServerDto>>(
                HttpMethod.Get, "api/Vpn/servers", null, null, AppJsonContext.Default.ListVpnServerDto, true, ct).ConfigureAwait(false);

            if (result.Data is { Count: > 0 } serverList)
            {
                _ = NormalizeServers(serverList);
            }

            if (result is { Success: true, Data.Count: > 0 })
            {
                return result;
            }

            if (IsUnauthorizedPending || await AuthManager.GetAuthHeaderAsync().ConfigureAwait(false) is null)
            {
                break;
            }

            if (PlatformServices.Connectivity != null &&
                (attempt >= maxRetries || PlatformServices.Connectivity.NetworkAccess == AppNetworkAccess.None))
            {
                break;
            }

            await Task.Delay(attempt * 600, ct).ConfigureAwait(false);
        }

        try
        {
            foreach (var discoveryUrl in AppConfig.RemoteServersDiscoveryUrls)
            {
                try
                {
                    using var rawCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    rawCts.CancelAfter(TimeSpan.FromSeconds(3.5));
                    var rawJson = await s_discoveryClient.Value.GetStringAsync(discoveryUrl, rawCts.Token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(rawJson))
                    {
                        continue;
                    }

                    var discovered = JsonSerializer.Deserialize(rawJson, AppJsonContext.Default.ListVpnServerDto);
                    if (discovered is { Count: > 0 })
                    {
                        return (true, NormalizeServers(discovered), null);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or TimeoutException
                                               or OperationCanceledException or JsonException or InvalidOperationException
                                               or AuthenticationException)
                {
                    //подавление ошибки запроса к зеркалу обнаружения серверов
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or TimeoutException
                                       or OperationCanceledException or JsonException or InvalidOperationException
                                       or AuthenticationException)
        {
            AppLogger.LogWarning($"[API-DISCOVERY] Remote discovery attempt failed: {ex.Message}");
        }

        AppLogger.LogWarning(
            $"[API-DISCOVERY] Remote servers discovery failed. Falling back to {AppConfig.BootstrapServers.Count} built-in bootstrap servers.");
        return (true, NormalizeServers([.. AppConfig.BootstrapServers]), null);
    }

    //получение хэша сертификата для ssl pinning
    public async Task<(bool Success, CertHashResponse? Data, string? Error)> GetCertHashAsync(CancellationToken ct = default) =>
        await SendRequestAsync<object, CertHashResponse>(
            HttpMethod.Get, "api/Vpn/cert-hash", null, null, AppJsonContext.Default.CertHashResponse, false, ct).ConfigureAwait(false);

    //проверка покупки google play
    public async Task<(bool Success, string? Error)> VerifyGooglePurchaseAsync(string productId, string purchaseToken, string? orderId,
        CancellationToken ct = default)
    {
        var request = new GooglePurchaseVerifyRequest(productId, purchaseToken, orderId);
        return await PostAsync("api/Payment/google-verify", request, AppJsonContext.Default.GooglePurchaseVerifyRequest, true, ct)
            .ConfigureAwait(false);
    }

    //получение реферального кода
    public async Task<(bool Success, ReferralCodeResponse? Data, string? Error)> GetMyReferralCodeAsync(CancellationToken ct = default) =>
        await SendRequestAsync<object, ReferralCodeResponse>(
                HttpMethod.Get, "api/Referral/my-code", null, null, AppJsonContext.Default.ReferralCodeResponse, true, ct)
            .ConfigureAwait(false);

    //активация реферального кода
    public async Task<(bool Success, MessageResponse? Data, string? Error)> ActivateReferralCodeAsync(string code,
        CancellationToken ct = default) =>
        await PostWithResponseAsync(
            "api/Referral/activate", new ActivateReferralRequest(code), AppJsonContext.Default.ActivateReferralRequest,
            AppJsonContext.Default.MessageResponse, true, ct).ConfigureAwait(false);

    //отправка отчета телеметрии на сервер
    public async Task<bool> PostTelemetryAsync(TelemetryDto telemetry, CancellationToken ct = default)
    {
        var (success, _) = await PostAsync(
            "api/Vpn/telemetry",
            telemetry,
            AppJsonContext.Default.TelemetryDto,
            false,
            ct).ConfigureAwait(false);
        return success;
    }

    //отправка отчета об инциденте подключения через основной конвейер с обходом dpi
    public async Task<bool> PostIncidentAsync(IncidentReportDto incident, CancellationToken ct = default)
    {
        var (success, _) = await PostAsync(
            "api/Vpn/incident",
            incident,
            AppJsonContext.Default.IncidentReportDto,
            false,
            ct).ConfigureAwait(false);
        return success;
    }
}
