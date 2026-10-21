namespace obxodka.Services;

public sealed class ApiService(HttpClient client)
{
    public static event Action? OnUnauthorized;

    private static readonly Lazy<HttpClient> t_fallbackNative = new(() => new HttpClient(new HttpClientHandler())
    {
        Timeout = TimeSpan.FromSeconds(8),
        DefaultRequestVersion = new Version(1, 1)
    });

    private static readonly Lazy<HttpClient> t_fallbackTls12 = new(() => new HttpClient(new SocketsHttpHandler
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            EnabledSslProtocols = SslProtocols.Tls12
        },
        UseProxy = false
    })
    {
        Timeout = TimeSpan.FromSeconds(8),
        DefaultRequestVersion = new Version(1, 1)
    });

    private static readonly Lazy<HttpClient> t_fallbackHttp2 = new(() => new HttpClient(new SocketsHttpHandler
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            EnabledSslProtocols = SslProtocols.None
        },
        UseProxy = false
    })
    {
        Timeout = TimeSpan.FromSeconds(8),
        DefaultRequestVersion = new Version(2, 0)
    });

    private static readonly ConcurrentDictionary<string, IPAddress> t_dohCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Lazy<HttpClient> t_dohClient = new(() => new HttpClient(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(2),
        SslOptions = new SslClientAuthenticationOptions
        {
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        }
    })
    {
        Timeout = TimeSpan.FromSeconds(2)
    });

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

    private static async ValueTask<IPAddress> ResolveHostAntiPoisonAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literalIp))
        {
            return literalIp;
        }

        if (t_dohCache.TryGetValue(host, out var cachedIp))
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
                    t_dohCache[host] = addr;
                    return addr;
                }
            }
        }
        catch { }

        string[] dohEndpoints =
        [
            $"https://1.1.1.1/dns-query?name={Uri.EscapeDataString(host)}&type=A",
            $"https://8.8.8.8/resolve?name={Uri.EscapeDataString(host)}&type=A"
        ];

        foreach (var dohUrl in dohEndpoints)
        {
            try
            {
                using var dohCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                dohCts.CancelAfter(TimeSpan.FromMilliseconds(1800));
                using var req = new HttpRequestMessage(HttpMethod.Get, dohUrl);
                req.Headers.Accept.ParseAdd("application/dns-json");
                using var resp = await t_dohClient.Value.SendAsync(req, dohCts.Token).ConfigureAwait(false);
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
                        t_dohCache[host] = dohIp;
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
            t_dohCache[host] = fallbackIp;
            return fallbackIp;
        }

        throw new SocketException((int)SocketError.HostNotFound);
    }

    private static readonly Lazy<HttpClient> t_fallbackDirectDpi = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = AppSecrets.GetRandomSni(),
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                    CertificateValidator.ValidateServerCertificate(cert, chain, errors)
            },
            ConnectCallback = async (_, cToken) =>
            {
                var targetIp = IPAddress.Parse(AppConfig.DirectServerIp);
                int[] fallbackPorts = [443, 8443, 2053];
                Exception? lastConnectEx = null;

                foreach (var port in fallbackPorts)
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
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

        request.Headers.Add("X-Device-Hwid", DeviceHelper.GetHwid());
    }

    public static SocketsHttpHandler CreateDefaultHandler() => new()
    {
        UseProxy = false,
        SslOptions = new SslClientAuthenticationOptions
        {
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        },
        ConnectCallback = async (context, cToken) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
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
        if (Connectivity.Current.NetworkAccess == AppNetworkAccess.None)
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

        HttpClient[] domainClients = [client, t_fallbackNative.Value, t_fallbackTls12.Value, t_fallbackHttp2.Value];
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
                            MainThread.BeginInvokeOnMainThread(() => OnUnauthorized?.Invoke());
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
                    Shared.Logging.AppLogger.LogWarning($"[API] {method} {reqUrl} direct attempt failed: {ex.Message}{innerInfo}");
                    var isTlsBlock = ex is HttpRequestException && (ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("handshake", StringComparison.OrdinalIgnoreCase) || ex.InnerException is AuthenticationException or SocketException);
                    if (isTlsBlock)
                    {
                        Shared.Logging.AppLogger.LogWarning($"[API-DPI] ISP/TSPU TLS block detected on {baseCandidate}. Fast-tracking to DPI bypass...");
                        break;
                    }
                }
            }
        }

        string[] directHosts = [AppSecrets.GetRandomSni(), "obxodka.one", "api.octocore.dev"];
        foreach (var directHost in directHosts)
        {
            try
            {
                var directUrl = $"https://{AppConfig.DirectServerIp}/{url.TrimStart('/')}";
                using var request = new HttpRequestMessage(method, directUrl);
                request.Headers.Host = directHost;
                if (body is not null && requestInfo is not null)
                {
                    request.Content = JsonContent.Create(body, requestInfo);
                }

                await PrepareRequestAsync(request, includeAuth).ConfigureAwait(false);
                var response = await t_fallbackDirectDpi.Value.SendAsync(request, ct).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    try
                    {
                        MainThread.BeginInvokeOnMainThread(() => OnUnauthorized?.Invoke());
                    }
                    catch { }

                    return (false, null, "Сессия истекла или устройство было удалено.");
                }

                if (response.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"[API-DPI] Request {method} {url} succeeded via DPI bypass stream (IP {AppConfig.DirectServerIp}, Host {directHost})");
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
                Shared.Logging.AppLogger.LogError($"[API DIRECT DPI ERROR] {method} {directHost}: {ex.Message}", ex);
            }
        }

        var baseEx = lastEx?.GetBaseException();
        var message = baseEx is not null && baseEx != lastEx ? $"{lastEx!.Message} ({baseEx.Message})" : lastEx?.Message;
        return (false, null, message ?? "Не удалось связаться с сервером");
    }

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

    public async Task<(bool Success, string? Url, string? Error)> GeneratePaymentLinkAsync(decimal amount, CancellationToken ct = default)
    {
        var amountStr = amount.ToString(CultureInfo.InvariantCulture);
        var url = $"api/payment/generate?amount={amountStr}";

        var (success, data, error) = await SendRequestAsync<object, PaymentLinkResponse>(
            HttpMethod.Get, url, null, null, AppJsonContext.Default.PaymentLinkResponse, includeAuth: true, ct: ct);

        if (success && data is { Url: { Length: > 0 } paymentUrl })
        {
            return (true, paymentUrl, null);
        }

        return (false, null, error ?? "Unknown error generating payment link");
    }

    public async Task<(bool Success, UserProfileResponse? Data, string? Error)> GetProfileAsync(int maxRetries = 2, CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            var result = await SendRequestAsync<object, UserProfileResponse>(
                HttpMethod.Get, "api/Auth/me", null, null, AppJsonContext.Default.UserProfileResponse, includeAuth: true, ct: ct).ConfigureAwait(false);

            if (result.Success || attempt >= maxRetries || Connectivity.Current.NetworkAccess == AppNetworkAccess.None)
            {
                return result;
            }

            await Task.Delay(attempt * 500, ct).ConfigureAwait(false);
        }

        return (false, null, "Не удалось получить профиль");
    }

    public async Task<(bool Success, string? Error)> RequestCodeAsync(EmailAuthRequest request, CancellationToken ct = default) =>
        await PostAsync("api/Auth/request-code", request, AppJsonContext.Default.EmailAuthRequest, includeAuth: false, ct: ct).ConfigureAwait(false);

    public async Task<(bool Success, LoginResponse? Data, string? Error)> VerifyCodeAsync(EmailVerifyRequest request, CancellationToken ct = default) =>
        await PostWithResponseAsync(
            "api/Auth/verify-code", request, AppJsonContext.Default.EmailVerifyRequest, AppJsonContext.Default.LoginResponse, includeAuth: false, ct: ct).ConfigureAwait(false);

    public async Task<(bool Success, List<DeviceItem>? Devices, string? Error)> GetDevicesAsync(CancellationToken ct = default) =>
        await SendRequestAsync<object, List<DeviceItem>>(
            HttpMethod.Get, "api/Auth/devices", null, null, AppJsonContext.Default.ListDeviceItem, includeAuth: true, ct: ct).ConfigureAwait(false);

    public async Task<(bool Success, string? Error)> RemoveDeviceAsync(string hwid, CancellationToken ct = default)
    {
        var url = $"api/Auth/devices/{Uri.EscapeDataString(hwid)}";
        var (success, _, error) = await SendRequestAsync<object, object>(
            HttpMethod.Delete, url, null, null, null, includeAuth: true, ct: ct).ConfigureAwait(false);
        return (success, error);
    }

    public async Task<(bool Success, string? Error)> DeleteAccountAsync(CancellationToken ct = default)
    {
        var (success, _, error) = await SendRequestAsync<object, object>(
            HttpMethod.Delete, "api/Auth/delete-user", null, null, null, includeAuth: true, ct: ct).ConfigureAwait(false);
        return (success, error);
    }

    public async Task StopVpnOnServerAsync(CancellationToken ct = default) =>
        _ = await PostAsync<object>("api/Vpn/stop", null, null, includeAuth: true, ct: ct).ConfigureAwait(false);

    public async Task<(bool Success, List<VpnServerDto>? Servers, string? Error)> GetServersAsync(int maxRetries = 3, CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            var result = await SendRequestAsync<object, List<VpnServerDto>>(
                HttpMethod.Get, "api/Vpn/servers", null, null, AppJsonContext.Default.ListVpnServerDto, includeAuth: true, ct: ct).ConfigureAwait(false);

            if (result.Success && result.Data is { Count: > 0 })
            {
                return result;
            }

            if (attempt >= maxRetries || Connectivity.Current.NetworkAccess == AppNetworkAccess.None)
            {
                break;
            }

            await Task.Delay(attempt * 600, ct).ConfigureAwait(false);
        }

        try
        {
            using var rawCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            rawCts.CancelAfter(TimeSpan.FromSeconds(4));
            using var rawClient = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            string? rawJson = null;
            try
            {
                rawJson = await rawClient.GetStringAsync(AppConfig.RemoteServersDiscoveryUrl, rawCts.Token).ConfigureAwait(false);
            }
            catch
            {
                rawJson = await rawClient.GetStringAsync(AppConfig.RemoteServersDiscoveryFallbackUrl, rawCts.Token).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(rawJson))
            {
                var discovered = JsonSerializer.Deserialize(rawJson, AppJsonContext.Default.ListVpnServerDto);
                if (discovered is { Count: > 0 })
                {
                    return (true, discovered, null);
                }
            }
        }
        catch (Exception ex)
        {
            Shared.Logging.AppLogger.LogWarning($"[API-DISCOVERY] Remote discovery attempt failed: {ex.Message}");
        }

        Shared.Logging.AppLogger.LogWarning($"[API-DISCOVERY] Remote servers discovery failed. Falling back to {AppConfig.BootstrapServers.Count} built-in bootstrap servers.");
        return (true, AppConfig.BootstrapServers, null);
    }

    public async Task<(bool Success, CertHashResponse? Data, string? Error)> GetCertHashAsync(CancellationToken ct = default) =>
        await SendRequestAsync<object, CertHashResponse>(
            HttpMethod.Get, "api/Vpn/cert-hash", null, null, AppJsonContext.Default.CertHashResponse, includeAuth: false, ct: ct).ConfigureAwait(false);

    public async Task<(bool Success, string? Error)> VerifyGooglePurchaseAsync(string productId, string purchaseToken, string? orderId, CancellationToken ct = default)
    {
        var request = new GooglePurchaseVerifyRequest(productId, purchaseToken, orderId);
        return await PostAsync("api/Payment/google-verify", request, AppJsonContext.Default.GooglePurchaseVerifyRequest, includeAuth: true, ct: ct).ConfigureAwait(false);
    }

    public async Task<(bool Success, ReferralCodeResponse? Data, string? Error)> GetMyReferralCodeAsync(CancellationToken ct = default) =>
        await SendRequestAsync<object, ReferralCodeResponse>(
            HttpMethod.Get, "api/Referral/my-code", null, null, AppJsonContext.Default.ReferralCodeResponse, includeAuth: true, ct: ct).ConfigureAwait(false);

    public async Task<(bool Success, MessageResponse? Data, string? Error)> ActivateReferralCodeAsync(string code, CancellationToken ct = default) =>
        await PostWithResponseAsync(
            "api/Referral/activate", new ActivateReferralRequest(code), AppJsonContext.Default.ActivateReferralRequest, AppJsonContext.Default.MessageResponse, includeAuth: true, ct: ct).ConfigureAwait(false);
}

