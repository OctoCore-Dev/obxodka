namespace obxodka.Services;

public sealed class AuthManager
{
    private static readonly SemaphoreSlim t_fileLock = new(1, 1);
    private static UserSession? t_cachedSession;
    private static AuthenticationHeaderValue? t_cachedAuthHeader;
    public static async ValueTask<AuthenticationHeaderValue?> GetAuthHeaderAsync()
    {
        if (t_cachedAuthHeader != null)
        {
            return t_cachedAuthHeader;
        }
        _ = await LoadSessionAsync().ConfigureAwait(false);
        return t_cachedAuthHeader;
    }
    public static async Task SaveSessionAsync(UserSession session)
    {
        await t_fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            t_cachedSession = session;
            t_cachedAuthHeader = string.IsNullOrEmpty(session.JwtToken)
                ? null
                : new AuthenticationHeaderValue("Bearer", session.JwtToken);
            Preferences.Default.Set("user_email", session.Email ?? string.Empty);
            Preferences.Default.Set("user_is_logged", session.IsLoggedIn);
            Preferences.Default.Set("user_sub_until", session.SubscriptionUntil?.Ticks ?? 0);
            if (!string.IsNullOrEmpty(session.Password))
            {
                await SecureStorage.Default.SetAsync("user_password", session.Password).ConfigureAwait(false);
            }
            if (!string.IsNullOrEmpty(session.JwtToken))
            {
                await SecureStorage.Default.SetAsync("user_jwt", session.JwtToken).ConfigureAwait(false);
            }
            if (!string.IsNullOrEmpty(session.VpnConfig))
            {
                await SecureStorage.Default.SetAsync("user_vpn_config", session.VpnConfig).ConfigureAwait(false);
            }
            Debug.WriteLine("[AUTH SUCCESS] Сессия зашифрована в Keystore (Android/iOS)");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AUTH SAVE ERROR]: {ex.Message}");
        }
        finally
        {
            _ = t_fileLock.Release();
        }
    }
    public static async Task<UserSession> LoadSessionAsync()
    {
        if (t_cachedSession != null)
        {
            return t_cachedSession;
        }
        await t_fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (t_cachedSession != null)
            {
                return t_cachedSession;
            }
            var isLoggedIn = Preferences.Default.Get("user_is_logged", false);
            if (!isLoggedIn)
            {
                t_cachedSession = new UserSession { IsLoggedIn = false };
                t_cachedAuthHeader = null;
                return t_cachedSession;
            }
            var jwt = await SecureStorage.Default.GetAsync("user_jwt").ConfigureAwait(false) ?? string.Empty;
            var pass = await SecureStorage.Default.GetAsync("user_password").ConfigureAwait(false) ?? string.Empty;
            var vpnConf = await SecureStorage.Default.GetAsync("user_vpn_config").ConfigureAwait(false) ?? string.Empty;
            var subTicks = Preferences.Default.Get("user_sub_until", 0L);
            if (string.IsNullOrEmpty(jwt))
            {
                ClearSessionDataInternal();
                t_cachedSession = new UserSession { IsLoggedIn = false };
                t_cachedAuthHeader = null;
                return t_cachedSession;
            }
            var session = new UserSession
            {
                Email = Preferences.Default.Get("user_email", string.Empty),
                IsLoggedIn = true,
                Password = pass,
                JwtToken = jwt,
                VpnConfig = vpnConf,
                SubscriptionUntil = new DateTime(subTicks, DateTimeKind.Utc)
            };
            t_cachedSession = session;
            t_cachedAuthHeader = new AuthenticationHeaderValue("Bearer", jwt);
            return session;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AUTH LOAD ERROR]: {ex.Message}");
            ClearSessionDataInternal();
            t_cachedSession = new UserSession { IsLoggedIn = false };
            t_cachedAuthHeader = null;
            return t_cachedSession;
        }
        finally
        {
            _ = t_fileLock.Release();
        }
    }
    public static async Task ClearSessionAsync()
    {
        await t_fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            t_cachedSession = null;
            t_cachedAuthHeader = null;
            ClearSessionDataInternal();
            Debug.WriteLine("[AUTH] Сессия полностью очищена.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AUTH CLEAR ERROR]: {ex.Message}");
        }
        finally
        {
            _ = t_fileLock.Release();
        }
    }
    private static void ClearSessionDataInternal()
    {
        Preferences.Default.Remove("user_email");
        Preferences.Default.Remove("user_is_logged");
        Preferences.Default.Remove("user_sub_until");
        _ = SecureStorage.Default.Remove("user_password");
        _ = SecureStorage.Default.Remove("user_jwt");
        _ = SecureStorage.Default.Remove("user_vpn_config");
    }
    public static async Task RemoveCurrentDeviceFromServerAsync()
    {
        try
        {
            var session = await LoadSessionAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(session.JwtToken))
            {
                return;
            }
            var hwid = DeviceHelper.GetHwid();
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.JwtToken);
            var fullUrl = AppConfig.ApiUrl($"api/Auth/devices/{Uri.EscapeDataString(hwid)}");
            _ = await client.DeleteAsync(fullUrl).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AUTH REMOVE DEVICE ERROR]: {ex.Message}");
        }
    }
}
