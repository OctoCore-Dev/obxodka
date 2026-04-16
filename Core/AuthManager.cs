namespace obxodka.Core;
internal sealed class UserSession
{
    public string? Email { get; set; }
    public string? Password { get; set; }
    public string? JwtToken { get; set; }
    public bool IsLoggedIn { get; set; }
}
internal sealed class AuthManager
{
    private static readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);
    private static readonly HttpClient _client
        = new HttpClient { BaseAddress = new Uri(AppConfig.ApiBaseUrl), Timeout = TimeSpan.FromSeconds(15) };
#if WINDOWS
    private static readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Obxodka", "session.dat");
    private static readonly byte[] _entropy = Encoding.UTF8.GetBytes("OctoCore_Security_Salt_2026");
#endif
    public static async Task SaveSessionAsync(UserSession session)
    {
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
#if WINDOWS
            Debug.WriteLine($"[DEBUG] Пытаемся сохранить файл в: {_filePath}");
            string json = JsonSerializer.Serialize(session);
            byte[] data = Encoding.UTF8.GetBytes(json);
            byte[] encrypted = ProtectedData.Protect(data, _entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await File.WriteAllBytesAsync(_filePath, encrypted).ConfigureAwait(false);
            if (File.Exists(_filePath))
            {
                Debug.WriteLine("[DEBUG] Файл успешно создан и лежит на диске!");
            }
#else
            Preferences.Default.Set("user_email", session.Email ?? "");
            Preferences.Default.Set("user_is_logged", session.IsLoggedIn);
            if (!string.IsNullOrEmpty(session.Password))
                await SecureStorage.Default.SetAsync("user_password", session.Password);
            if (!string.IsNullOrEmpty(session.JwtToken))
                await SecureStorage.Default.SetAsync("user_jwt", session.JwtToken);
#endif
            Debug.WriteLine($"[AUTH SUCCESS] Сессия сохранена: {session.Email}");
        }
        catch (IOException ioEx)
        {
            Debug.WriteLine($"[AUTH I/O ERROR]: {ioEx.Message}");
            throw;
        }
        catch (UnauthorizedAccessException authEx)
        {
            Debug.WriteLine($"[AUTH ACCESS ERROR]: {authEx.Message}");
            throw;
        }
        catch (JsonException jsonEx)
        {
            Debug.WriteLine($"[AUTH JSON ERROR]: {jsonEx.Message}");
        }
        catch (Exception)
        {
            throw;
        }
        finally { _fileLock.Release(); }
    }
    public static async Task<UserSession> LoadSessionAsync()
    {
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
#if WINDOWS
            if (!File.Exists(_filePath)) return new UserSession { IsLoggedIn = false };
            byte[] encrypted = await File.ReadAllBytesAsync(_filePath).ConfigureAwait(false);
            byte[] decrypted = ProtectedData.Unprotect(encrypted, _entropy, DataProtectionScope.CurrentUser);
            string json = Encoding.UTF8.GetString(decrypted);
            var session = JsonSerializer.Deserialize<UserSession>(json);
            return session ?? new UserSession { IsLoggedIn = false };
#else
            bool isLoggedIn = Preferences.Default.Get("user_is_logged", false);
            if (!isLoggedIn) return new UserSession { IsLoggedIn = false };
            string jwt = await SecureStorage.Default.GetAsync("user_jwt") ?? "";
            string pass = await SecureStorage.Default.GetAsync("user_password") ?? "";
            if (string.IsNullOrEmpty(jwt))
            {
                Preferences.Default.Remove("user_email");
                Preferences.Default.Remove("user_is_logged");
                SecureStorage.Default.Remove("user_password");
                SecureStorage.Default.Remove("user_jwt");
                return new UserSession { IsLoggedIn = false };
            }
            return new UserSession
            {
                Email = Preferences.Default.Get("user_email", ""),
                IsLoggedIn = true,
                Password = pass,
                JwtToken = jwt
            };
#endif
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AUTH LOAD ERROR]: {ex.Message}");
#if !WINDOWS
            Preferences.Default.Remove("user_email");
            Preferences.Default.Remove("user_is_logged");
            SecureStorage.Default.Remove("user_password");
            SecureStorage.Default.Remove("user_jwt");
#endif
            return new UserSession { IsLoggedIn = false };
        }
        finally { _fileLock.Release(); }
    }
    public static void ClearSession()
    {
        _fileLock.Wait();
        try
        {
#if WINDOWS
            if (File.Exists(_filePath)) File.Delete(_filePath);
#else
            Preferences.Default.Remove("user_email");
            Preferences.Default.Remove("user_is_logged");
            SecureStorage.Default.Remove("user_password");
            SecureStorage.Default.Remove("user_jwt");
#endif
            Debug.WriteLine("[AUTH] Сессия полностью очищена.");
        }
        catch (IOException ioEx)
        {
            Debug.WriteLine($"[AUTH I/O ERROR]: {ioEx.Message}");
            throw;
        }
        catch (UnauthorizedAccessException authEx)
        {
            Debug.WriteLine($"[AUTH ACCESS ERROR]: {authEx.Message}");
            throw;
        }
        catch (JsonException jsonEx)
        {
            Debug.WriteLine($"[AUTH JSON ERROR]: {jsonEx.Message}");
        }
        catch (Exception)
        {
            throw;
        }
        finally { _fileLock.Release(); }
    }
    public static async Task<bool> PingDeviceAsync(string hwid)
    {
        try
        {
            var session = await LoadSessionAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(session.JwtToken)) return false;
            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, $"{AppConfig.ApiBaseUrl}api/Auth/verify-device?hwid={Uri.EscapeDataString(hwid)}");
            requestMessage.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.JwtToken);
            var response = await _client.SendAsync(requestMessage).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (IOException) { throw; }
        catch (UnauthorizedAccessException) { throw; }
        catch (Exception) { throw; }
    }
    public static async Task RemoveCurrentDeviceFromServerAsync()
    {
        try
        {
            var session = await LoadSessionAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(session.JwtToken)) return;
            var hwid = DeviceHelper.GetHwid();
            var requestUri = new Uri(_client.BaseAddress!, $"api/Auth/devices/{Uri.EscapeDataString(hwid)}");
            _client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.JwtToken);
            await _client.DeleteAsync(requestUri).ConfigureAwait(false);
        }
        catch (IOException ioEx)
        {
            Debug.WriteLine($"[AUTH I/O ERROR]: {ioEx.Message}");
            throw;
        }
        catch (UnauthorizedAccessException authEx)
        {
            Debug.WriteLine($"[AUTH ACCESS ERROR]: {authEx.Message}");
            throw;
        }
        catch (JsonException jsonEx)
        {
            Debug.WriteLine($"[AUTH JSON ERROR]: {jsonEx.Message}");
        }
        catch (Exception)
        {
            throw;
        }
    }
}