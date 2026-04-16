namespace obxodka.Core;
internal sealed class ApiService(HttpClient client)
{
    private void SignRequest(string endpointUrl)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string path = new Uri(client.BaseAddress!, endpointUrl).AbsolutePath;
        string dataToSign = $"{path}:{timestamp}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(AppSecrets.ApiSignatureKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(dataToSign));
        string signature = Convert.ToBase64String(hash);
        client.DefaultRequestHeaders.Remove("X-App-Timestamp");
        client.DefaultRequestHeaders.Remove("X-App-Signature");
        client.DefaultRequestHeaders.Add("X-App-Timestamp", timestamp.ToString());
        client.DefaultRequestHeaders.Add("X-App-Signature", signature);
    }
    public async Task<(bool Success, string? Error)> PostAsync<TRequest>(string url, TRequest? body = null)
        where TRequest : class
    {
        try
        {
            var session = await AuthManager.LoadSessionAsync().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(session.JwtToken))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.JwtToken);
            SignRequest(url);
            var requestUri = new Uri(client.BaseAddress!, url);
            HttpResponseMessage response = body != null
                ? await client.PostAsJsonAsync(requestUri, body).ConfigureAwait(false)
                : await client.PostAsync(requestUri, null).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return (true, null);
            var error = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return (false, error ?? $"Error: {response.StatusCode}");
        }
        catch (IOException) { throw; }
        catch (UnauthorizedAccessException) { throw; }
        catch (Exception) { throw; }
    }
    public async Task<(bool Success, string? Error)> DeleteAccountAsync(string email)
    {
        try
        {
            var session = await AuthManager.LoadSessionAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(session.JwtToken)) return (false, "Сессия не найдена");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.JwtToken);
            var url = $"api/Auth/delete-user?username={Uri.EscapeDataString(email)}";
            SignRequest(url);
            var requestUri = new Uri(client.BaseAddress!, url);
            var response = await client.DeleteAsync(requestUri).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return (true, null);
            var error = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return (false, error ?? "Ошибка сервера");
        }
        catch (IOException) { throw; }
        catch (UnauthorizedAccessException) { throw; }
        catch (Exception) { throw; }
    }
    public async Task<(bool Success, string? Error)> RegisterAsync(AuthRequest request)
    {
        try
        {
            request.DeviceName = $"{DeviceInfo.Current.Platform} | {DeviceInfo.Current.Name}";
            var (success, error) = await PostAsync("api/Auth/register", request).ConfigureAwait(false);
            return (success, error);
        }
        catch (IOException) { throw; }
        catch (UnauthorizedAccessException) { throw; }
        catch (Exception) { throw; }
    }
    public async Task<(bool Success, TResponse? Data, string? Error)> PostWithResponseAsync<TRequest, TResponse>(string url, TRequest? body = null)
        where TRequest : class
    {
        try
        {
            var session = await AuthManager.LoadSessionAsync().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(session.JwtToken))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.JwtToken);
            SignRequest(url);
            var requestUri = new Uri(client.BaseAddress!, url);
            HttpResponseMessage response = body != null
                ? await client.PostAsJsonAsync(requestUri, body).ConfigureAwait(false)
                : await client.PostAsync(requestUri, null).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var data = await response.Content.ReadFromJsonAsync<TResponse>().ConfigureAwait(false);
                return (true, data, null);
            }
            return (false, default, $"Status: {response.StatusCode}");
        }
        catch (IOException) { throw; }
        catch (UnauthorizedAccessException) { throw; }
        catch (Exception) { throw; }
    }
    public async Task<(bool Success, List<DeviceItem>? Devices, string? Error)> GetDevicesAsync()
    {
        try
        {
            var session = await AuthManager.LoadSessionAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(session.JwtToken)) return (false, null, "No token");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.JwtToken);
            var url = "api/Auth/devices";
            SignRequest(url);
            var requestUri = new Uri(client.BaseAddress!, url);
            var response = await client.GetAsync(requestUri).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var devices = await response.Content.ReadFromJsonAsync<List<DeviceItem>>().ConfigureAwait(false);
                return (true, devices, null);
            }
            return (false, null, $"Status: {response.StatusCode}");
        }
        catch (IOException) { throw; }
        catch (UnauthorizedAccessException) { throw; }
        catch (Exception) { throw; }
    }
    public async Task<(bool Success, string? Error)> RemoveDeviceAsync(string hwid)
    {
        try
        {
            var session = await AuthManager.LoadSessionAsync().ConfigureAwait(false);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.JwtToken);
            var url = $"api/Auth/devices/{Uri.EscapeDataString(hwid)}";
            SignRequest(url);
            var requestUri = new Uri(client.BaseAddress!, url);
            var response = await client.DeleteAsync(requestUri).ConfigureAwait(false);
            return (response.IsSuccessStatusCode, response.IsSuccessStatusCode ? null : "Server error");
        }
        catch (IOException) { throw; }
        catch (UnauthorizedAccessException) { throw; }
        catch (Exception) { throw; }
    }
    public async Task<(bool IsActive, long RemainingSeconds)> SyncVpnStatusAsync()
    {
        var (success, data, _) = await PostWithResponseAsync<object, VpnStatusResponse>("api/Vpn/ping").ConfigureAwait(false);
        return success && data != null ? (data.IsActive, data.RemainingSeconds) : (false, 0);
    }
    public async Task<(bool Success, LoginResponse? Data, string? Error)> LoginAsync(AuthRequest request)
    {
        request.DeviceName = $"{DeviceInfo.Current.Platform} | {DeviceInfo.Current.Name}";
        return await PostWithResponseAsync<AuthRequest, LoginResponse>("api/Auth/login", request).ConfigureAwait(false);
    }
    public async Task<(bool Success, string? Error)> ChangePasswordAsync(string email, string oldP, string newP)
    {
        var url = $"api/Auth/change-password?username={Uri.EscapeDataString(email)}&oldPassword={Uri.EscapeDataString(oldP)}&newPassword={Uri.EscapeDataString(newP)}";
        return await PostAsync<object>(url, null).ConfigureAwait(false);
    }
    public async Task StopVpnOnServerAsync()
    {
        _ = await PostAsync<object>("api/Vpn/stop").ConfigureAwait(false);
    }
}