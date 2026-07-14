namespace obxodka.Services;

public sealed class ApiService(HttpClient client)
{
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
    }

    private async Task<(bool Success, TResponse? Data, string? Error)> SendRequestAsync<TRequest, TResponse>(
        HttpMethod method, string url, TRequest? body, JsonTypeInfo<TRequest>? requestInfo, JsonTypeInfo<TResponse>? responseInfo, bool includeAuth = true)
        where TRequest : class where TResponse : class
    {
        try
        {
            using var request = new HttpRequestMessage(method, url);
            if (body != null && requestInfo != null)
            {
                request.Content = JsonContent.Create(body, requestInfo);
            }
            await PrepareRequestAsync(request, includeAuth).ConfigureAwait(false);

            var response = await client.SendAsync(request).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                if (responseInfo != null)
                {
                    return (true, await response.Content.ReadFromJsonAsync(responseInfo).ConfigureAwait(false), null);
                }
                return (true, null, null);
            }
            return (false, null, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[API ERROR] {method} {url}: {ex.Message}");
            return (false, null, ex.Message);
        }
    }

    public async Task<(bool Success, string? Error)> PostAsync<TRequest>(string url, TRequest? body, JsonTypeInfo<TRequest>? typeInfo, bool includeAuth = true) where TRequest : class
    {
        var (success, _, error) = await SendRequestAsync<TRequest, object>(HttpMethod.Post, url, body, typeInfo, null, includeAuth).ConfigureAwait(false);
        return (success, error);
    }

    public async Task<(bool Success, TResponse? Data, string? Error)> PostWithResponseAsync<TRequest, TResponse>(string url, TRequest? body, JsonTypeInfo<TRequest> requestInfo, JsonTypeInfo<TResponse> responseInfo, bool includeAuth = true) where TRequest : class where TResponse : class =>
        await SendRequestAsync(HttpMethod.Post, url, body, requestInfo, responseInfo, includeAuth).ConfigureAwait(false);

    public async Task<(bool Success, UserProfileResponse? Data, string? Error)> GetProfileAsync() =>
        await SendRequestAsync<object, UserProfileResponse>(HttpMethod.Get, "api/Auth/me", null, null, AppJsonContext.Default.UserProfileResponse).ConfigureAwait(false);

    public async Task<(bool Success, string? Error)> RegisterAsync(AuthRequest request)
    {
        var authorizedRequest = request with { DeviceName = $"{DeviceInfo.Current.Platform} | {DeviceInfo.Current.Name}" };
        return await PostAsync("api/Auth/register", authorizedRequest, AppJsonContext.Default.AuthRequest, includeAuth: false).ConfigureAwait(false);
    }

    public async Task<(bool Success, LoginResponse? Data, string? Error)> LoginAsync(AuthRequest request)
    {
        var authorizedRequest = request with { DeviceName = $"{DeviceInfo.Current.Platform} | {DeviceInfo.Current.Name}" };
        return await PostWithResponseAsync("api/Auth/login", authorizedRequest, AppJsonContext.Default.AuthRequest, AppJsonContext.Default.LoginResponse, includeAuth: false).ConfigureAwait(false);
    }

    public async Task<(bool Success, List<DeviceItem>? Devices, string? Error)> GetDevicesAsync() =>
        await SendRequestAsync<object, List<DeviceItem>>(HttpMethod.Get, "api/Auth/devices", null, null, AppJsonContext.Default.ListDeviceItem).ConfigureAwait(false);

    public async Task<(bool Success, string? Error)> RemoveDeviceAsync(string hwid)
    {
        var url = $"api/Auth/devices/{Uri.EscapeDataString(hwid)}";
        var (success, _, error) = await SendRequestAsync<object, object>(HttpMethod.Delete, url, null, null, null).ConfigureAwait(false);
        return (success, error);
    }

    public async Task<(bool Success, string? Error)> ChangePasswordAsync(string email, string oldP, string newP)
    {
        var request = new ChangePasswordRequest(email, oldP, newP);
        return await PostAsync("api/Auth/change-password", request, AppJsonContext.Default.ChangePasswordRequest).ConfigureAwait(false);
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
}
