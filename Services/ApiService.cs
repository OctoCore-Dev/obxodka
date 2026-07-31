namespace obxodka.Services;

public sealed class ApiService(HttpClient client)
{
    public static event Action? OnUnauthorized;

    private static readonly Lazy<HttpClient> t_fallbackNative = new(() => new HttpClient(new HttpClientHandler())
    { Timeout = TimeSpan.FromSeconds(30), DefaultRequestVersion = new Version(1, 1) });

    private static readonly Lazy<HttpClient> t_fallbackTls12 = new(() => new HttpClient(new SocketsHttpHandler
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
        },
        UseProxy = false
    })
    { Timeout = TimeSpan.FromSeconds(30), DefaultRequestVersion = new Version(1, 1) });

    private static readonly Lazy<HttpClient> t_fallbackHttp2 = new(() => new HttpClient(new SocketsHttpHandler
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.None
        },
        UseProxy = false
    })
    { Timeout = TimeSpan.FromSeconds(30), DefaultRequestVersion = new Version(2, 0) });

    private static async ValueTask PrepareRequestAsync(HttpRequestMessage request, bool includeAuth = true)
    {
        if (includeAuth)
        {
            var authHeader = await AuthManager.GetAuthHeaderAsync().ConfigureAwait(false);
            if (authHeader != null)
            {
                request.Headers.Authorization = authHeader;
            }
        }
        request.Headers.Add("X-Device-Hwid", DeviceHelper.GetHwid());
    }

    private async Task<(bool Success, TResponse? Data, string? Error)> SendRequestAsync<TRequest, TResponse>(
        HttpMethod method, string url, TRequest? body, JsonTypeInfo<TRequest>? requestInfo, JsonTypeInfo<TResponse>? responseInfo, bool includeAuth = true)
        where TRequest : class where TResponse : class
    {
        var fullUrl = AppConfig.ApiUrl(url);
        var clientsToTry = new[] { client, t_fallbackNative.Value, t_fallbackTls12.Value, t_fallbackHttp2.Value };
        Exception? lastEx = null;

        foreach (var currentClient in clientsToTry)
        {
            try
            {
                using var request = new HttpRequestMessage(method, fullUrl);
                if (body != null && requestInfo != null)
                {
                    request.Content = JsonContent.Create(body, requestInfo);
                }
                await PrepareRequestAsync(request, includeAuth).ConfigureAwait(false);
                var response = await currentClient.SendAsync(request).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    try
                    { MainThread.BeginInvokeOnMainThread(() => OnUnauthorized?.Invoke()); }
                    catch { }
                    return (false, null, "Сессия истекла или устройство было удалено.");
                }

                if (response.IsSuccessStatusCode)
                {
                    if (responseInfo != null)
                    {
                        return (true, await response.Content.ReadFromJsonAsync(responseInfo).ConfigureAwait(false), null);
                    }

                    return (true, null, null);
                }

                if ((int)response.StatusCode < 500 && response.StatusCode != HttpStatusCode.BadGateway)
                {
                    var rawError = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    try
                    {
                        var errorObj = JsonSerializer.Deserialize(rawError, AppJsonContext.Default.MessageResponse);
                        if (!string.IsNullOrWhiteSpace(errorObj?.Message))
                        {
                            return (false, null, errorObj.Message);
                        }
                    }
                    catch { }
                    return (false, null, rawError);
                }
            }
            catch (Exception ex)
            {
                lastEx = ex;
                Debug.WriteLine($"[API ERROR] {method} {url} via fallback: {ex.Message}");
            }
        }

        var baseEx = lastEx?.GetBaseException();
        var msg = baseEx != null && baseEx != lastEx ? $"{lastEx!.Message} ({baseEx.Message})" : lastEx?.Message;
        return (false, null, msg ?? "Unknown network error");
    }

    public async Task<(bool Success, string? Error)> PostAsync<TRequest>(string url, TRequest? body, JsonTypeInfo<TRequest>? typeInfo, bool includeAuth = true) where TRequest : class
    {
        var (success, _, error) = await SendRequestAsync<TRequest, object>(HttpMethod.Post, url, body, typeInfo, null, includeAuth).ConfigureAwait(false);
        return (success, error);
    }

    public async Task<(bool Success, TResponse? Data, string? Error)> PostWithResponseAsync<TRequest, TResponse>(string url, TRequest? body, JsonTypeInfo<TRequest> requestInfo, JsonTypeInfo<TResponse> responseInfo, bool includeAuth = true) where TRequest : class where TResponse : class =>
        await SendRequestAsync(HttpMethod.Post, url, body, requestInfo, responseInfo, includeAuth).ConfigureAwait(false);

    public async Task<(bool Success, string? Url, string? Error)> GeneratePaymentLinkAsync(decimal amount)
    {
        var amountStr = amount.ToString(CultureInfo.InvariantCulture);
        var url = $"api/payment/generate?amount={amountStr}";

        var (success, data, error) = await SendRequestAsync<object, PaymentLinkResponse>(
            HttpMethod.Get, url, null, null, AppJsonContext.Default.PaymentLinkResponse);

        if (success && data != null && !string.IsNullOrEmpty(data.Url))
        {
            return (true, data.Url, null);
        }
        return (false, null, error ?? "Unknown error generating payment link");
    }

    public async Task<(bool Success, UserProfileResponse? Data, string? Error)> GetProfileAsync() =>
        await SendRequestAsync<object, UserProfileResponse>(HttpMethod.Get, "api/Auth/me", null, null, AppJsonContext.Default.UserProfileResponse).ConfigureAwait(false);

    public async Task<(bool Success, string? Error)> RequestCodeAsync(EmailAuthRequest request) => await PostAsync("api/Auth/request-code", request, AppJsonContext.Default.EmailAuthRequest, includeAuth: false).ConfigureAwait(false);

    public async Task<(bool Success, LoginResponse? Data, string? Error)> VerifyCodeAsync(EmailVerifyRequest request) => await PostWithResponseAsync("api/Auth/verify-code", request, AppJsonContext.Default.EmailVerifyRequest, AppJsonContext.Default.LoginResponse, includeAuth: false).ConfigureAwait(false);

    public async Task<(bool Success, List<DeviceItem>? Devices, string? Error)> GetDevicesAsync() =>
        await SendRequestAsync<object, List<DeviceItem>>(HttpMethod.Get, "api/Auth/devices", null, null, AppJsonContext.Default.ListDeviceItem).ConfigureAwait(false);

    public async Task<(bool Success, string? Error)> RemoveDeviceAsync(string hwid)
    {
        var url = $"api/Auth/devices/{Uri.EscapeDataString(hwid)}";
        var (success, _, error) = await SendRequestAsync<object, object>(HttpMethod.Delete, url, null, null, null).ConfigureAwait(false);
        return (success, error);
    }

    public async Task<(bool Success, string? Error)> DeleteAccountAsync()
    {
        var (success, _, error) = await SendRequestAsync<object, object>(HttpMethod.Delete, "api/Auth/delete-user", null, null, null).ConfigureAwait(false);
        return (success, error);
    }

    public async Task StopVpnOnServerAsync() =>
        _ = await PostAsync<object>("api/Vpn/stop", null, null).ConfigureAwait(false);

    public async Task<(bool Success, List<VpnServerDto>? Servers, string? Error)> GetServersAsync() =>
        await SendRequestAsync<object, List<VpnServerDto>>(HttpMethod.Get, "api/Vpn/servers", null, null, AppJsonContext.Default.ListVpnServerDto).ConfigureAwait(false);

    public async Task<(bool Success, Models.Responses.CertHashResponse? Data, string? Error)> GetCertHashAsync() =>
        await SendRequestAsync<object, Models.Responses.CertHashResponse>(HttpMethod.Get, "api/Vpn/cert-hash", null, null, AppJsonContext.Default.CertHashResponse, includeAuth: false).ConfigureAwait(false);
}
