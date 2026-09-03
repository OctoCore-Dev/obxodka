namespace obxodka.Services;

public sealed class ApiService(HttpClient client)
{
    public static event Action? OnUnauthorized;

    private static readonly Lazy<HttpClient> t_fallbackNative = new(() => new HttpClient(new HttpClientHandler())
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestVersion = new Version(1, 1)
    });

    private static readonly Lazy<HttpClient> t_fallbackTls12 = new(() => new HttpClient(new SocketsHttpHandler
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
        },
        UseProxy = false
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestVersion = new Version(1, 1)
    });

    private static readonly Lazy<HttpClient> t_fallbackHttp2 = new(() => new HttpClient(new SocketsHttpHandler
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.None
        },
        UseProxy = false
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestVersion = new Version(2, 0)
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
        if (Connectivity.Current.NetworkAccess != AppNetworkAccess.Internet)
        {
            return (false, null, "Нет подключения к интернету. Проверьте сеть и повторите попытку.");
        }

        var fullUrl = AppConfig.ApiUrl(url);
        HttpClient[] clientsToTry = [client, t_fallbackNative.Value, t_fallbackTls12.Value, t_fallbackHttp2.Value];
        Exception? lastEx = null;

        foreach (var currentClient in clientsToTry)
        {
            try
            {
                using var request = new HttpRequestMessage(method, fullUrl);
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
                    if (responseInfo is not null)
                    {
                        return (true, await response.Content.ReadFromJsonAsync(responseInfo, ct).ConfigureAwait(false), null);
                    }

                    return (true, null, null);
                }

                var rawError = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                try
                {
                    var errorObj = JsonSerializer.Deserialize(rawError, AppJsonContext.Default.MessageResponse);
                    if (errorObj is { Message: { Length: > 0 } msg })
                    {
                        return (false, null, msg);
                    }
                }
                catch { }

                if (!string.IsNullOrWhiteSpace(rawError))
                {
                    return (false, null, rawError);
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
                Debug.WriteLine($"[API ERROR] {method} {url} via fallback: {ex.Message}");
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

            if (result.Success || attempt >= maxRetries || Connectivity.Current.NetworkAccess != AppNetworkAccess.Internet)
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

            if (attempt >= maxRetries || Connectivity.Current.NetworkAccess != AppNetworkAccess.Internet)
            {
                return (false, result.Data, result.Error ?? "Не удалось получить список нод");
            }

            await Task.Delay(attempt * 600, ct).ConfigureAwait(false);
        }

        return (false, null, "Не удалось получить список нод");
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

    public async Task<(bool Success, ClaimRewardResponse? Data, string? Error)> ClaimReferralRewardAsync(string claimId, CancellationToken ct = default) =>
        await PostWithResponseAsync(
            "api/Referral/claim", new ClaimRewardRequest(claimId), AppJsonContext.Default.ClaimRewardRequest, AppJsonContext.Default.ClaimRewardResponse, includeAuth: true, ct: ct).ConfigureAwait(false);
}

