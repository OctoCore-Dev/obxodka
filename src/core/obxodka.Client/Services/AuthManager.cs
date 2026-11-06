namespace obxodka.Services;

//менеджер сессии пользователя и сохраненных токенов
public sealed class AuthManager
{
    private static readonly SemaphoreSlim s_fileLock = new(1, 1);
    private static UserSession? s_cachedSession;
    private static AuthenticationHeaderValue? s_cachedAuthHeader;

    //получение заголовка авторизации bearer
    public static async ValueTask<AuthenticationHeaderValue?> GetAuthHeaderAsync()
    {
        if (s_cachedAuthHeader is not null)
        {
            return s_cachedAuthHeader;
        }

        _ = await LoadSessionAsync().ConfigureAwait(false);
        return s_cachedAuthHeader;
    }

    //сохранение сессии пользователя в защищенное хранилище
    public static async Task SaveSessionAsync(UserSession session)
    {
        await s_fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            s_cachedSession = session;
            s_cachedAuthHeader = string.IsNullOrEmpty(session.JwtToken)
                ? null
                : new AuthenticationHeaderValue("Bearer", session.JwtToken);

            PlatformServices.Preferences.Set(AppPreferenceKeys.UserEmail, session.Email ?? string.Empty);
            PlatformServices.Preferences.Set(AppPreferenceKeys.UserIsLogged, session.IsLoggedIn);
            PlatformServices.Preferences.Set(AppPreferenceKeys.UserSubUntil, session.SubscriptionUntil?.Ticks ?? 0);
            PlatformServices.Preferences.Set(AppPreferenceKeys.UserBalanceSec, session.BalanceSeconds);

            if (!string.IsNullOrEmpty(session.Password))
            {
                await PlatformServices.SecureStorage.SetAsync(AppPreferenceKeys.UserPassword, session.Password).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(session.JwtToken))
            {
                await PlatformServices.SecureStorage.SetAsync(AppPreferenceKeys.UserJwt, session.JwtToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(session.VpnConfig))
            {
                await PlatformServices.SecureStorage.SetAsync(AppPreferenceKeys.UserVpnConfig, session.VpnConfig).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AUTH SAVE ERROR]: {ex.Message}");
        }
        finally
        {
            _ = s_fileLock.Release();
        }
    }

    //загрузка сессии из защищенного хранилища
    [SuppressMessage("Maintainability", "CA1508:Удалить неиспользуемый код", Justification = "Double-checked locking pattern")]
    public static async Task<UserSession> LoadSessionAsync()
    {
        if (s_cachedSession is not null)
        {
            return s_cachedSession;
        }

        await s_fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (s_cachedSession is not null)
            {
                return s_cachedSession;
            }

            var isLoggedIn = PlatformServices.Preferences.Get(AppPreferenceKeys.UserIsLogged, false);
            if (!isLoggedIn)
            {
                s_cachedSession = new UserSession { IsLoggedIn = false };
                s_cachedAuthHeader = null;
                return s_cachedSession;
            }

            var jwt = await PlatformServices.SecureStorage.GetAsync(AppPreferenceKeys.UserJwt).ConfigureAwait(false) ?? string.Empty;
            var pass = await PlatformServices.SecureStorage.GetAsync(AppPreferenceKeys.UserPassword).ConfigureAwait(false) ?? string.Empty;
            var vpnConf = await PlatformServices.SecureStorage.GetAsync(AppPreferenceKeys.UserVpnConfig).ConfigureAwait(false) ?? string.Empty;
            var subTicks = PlatformServices.Preferences.Get(AppPreferenceKeys.UserSubUntil, 0L);
            var balanceSec = PlatformServices.Preferences.Get(AppPreferenceKeys.UserBalanceSec, 0L);

            if (string.IsNullOrEmpty(jwt))
            {
                ClearSessionDataInternal();
                s_cachedSession = new UserSession { IsLoggedIn = false };
                s_cachedAuthHeader = null;
                return s_cachedSession;
            }

            var session = new UserSession
            {
                Email = PlatformServices.Preferences.Get(AppPreferenceKeys.UserEmail, string.Empty),
                IsLoggedIn = true,
                Password = pass,
                JwtToken = jwt,
                VpnConfig = vpnConf,
                SubscriptionUntil = subTicks > 0 ? new DateTime(subTicks, DateTimeKind.Utc) : null,
                BalanceSeconds = balanceSec
            };

            s_cachedSession = session;
            s_cachedAuthHeader = new AuthenticationHeaderValue("Bearer", jwt);
            return session;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AUTH LOAD ERROR]: {ex.Message}");
            ClearSessionDataInternal();
            s_cachedSession = new UserSession { IsLoggedIn = false };
            s_cachedAuthHeader = null;
            return s_cachedSession;
        }
        finally
        {
            _ = s_fileLock.Release();
        }
    }

    //очистка данных сессии при выходе
    public static async Task ClearSessionAsync()
    {
        await s_fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            s_cachedSession = null;
            s_cachedAuthHeader = null;
            ClearSessionDataInternal();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AUTH CLEAR ERROR]: {ex.Message}");
        }
        finally
        {
            _ = s_fileLock.Release();
        }
    }

    //удаление сохраненных данных авторизации
    private static void ClearSessionDataInternal()
    {
        PlatformServices.Preferences.Remove(AppPreferenceKeys.UserEmail);
        PlatformServices.Preferences.Remove(AppPreferenceKeys.UserIsLogged);
        PlatformServices.Preferences.Remove(AppPreferenceKeys.UserSubUntil);
        PlatformServices.Preferences.Remove(AppPreferenceKeys.UserBalanceSec);
        _ = PlatformServices.SecureStorage.Remove(AppPreferenceKeys.UserPassword);
        _ = PlatformServices.SecureStorage.Remove(AppPreferenceKeys.UserJwt);
        _ = PlatformServices.SecureStorage.Remove(AppPreferenceKeys.UserVpnConfig);
    }
}
