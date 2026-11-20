namespace obxodka.Pages;

public sealed partial class MainPage
{
    public Task TriggerBalanceSyncAsync() => SyncBalanceFromServerAsync();

    private static long ReadCachedRemainingSeconds()
    {
        var bal = PlatformServices.Preferences.Get(AppPreferenceKeys.UserBalanceSec, 0L);
        if (bal > 0)
        {
            return bal;
        }

        var subTicks = PlatformServices.Preferences.Get(AppPreferenceKeys.UserSubUntil, 0L);
        if (subTicks <= 0)
        {
            return 0;
        }

        var until = new DateTime(subTicks, DateTimeKind.Utc);
        return until > DateTime.UtcNow
            ? Math.Max(0, (long)(until - DateTime.UtcNow).TotalSeconds)
            : 0;
    }

    private void ApplySessionBalance(UserSession? session)
    {
        if (session is null)
        {
            return;
        }

        RemainingSeconds = session.BalanceSeconds > 0
            ? session.BalanceSeconds
            : session.SubscriptionUntil is { } until && until > DateTime.UtcNow
                ? Math.Max(0, (long)(until.ToUniversalTime() - DateTime.UtcNow).TotalSeconds)
                : 0;
        UpdateBalanceUi();
    }

    private void UpdateBalanceUi()
    {
        var timeText = TimeFormatHelper.FormatSeconds(RemainingSeconds, false);
        _tabContentVpn?.UpdateBalanceUi(timeText);
        _tabContentProfile?.UpdateBalance(RemainingSeconds);
    }

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        if (e.NetworkAccess == NetworkAccess.Internet)
        {
            MainThread.BeginInvokeOnMainThread(async void () =>
            {
                try
                {
                    if (_activeTab != "auth")
                    {
                        await SyncBalanceFromServerAsync();
                    }
                }
                catch
                {
                    // ignored
                }
            });
        }
    }

    private async Task SyncBalanceFromServerAsync(UserSession? session = null, int retryCount = 0)
    {
        session ??= await AuthManager.LoadSessionAsync();
        if (string.IsNullOrEmpty(session.JwtToken))
        {
            return;
        }

        if (RemainingSeconds <= 0 &&
            (session.BalanceSeconds > 0 || session.SubscriptionUntil is { } initUntil && initUntil > DateTime.UtcNow))
        {
            var initSec = session.BalanceSeconds > 0
                ? session.BalanceSeconds
                : session.SubscriptionUntil is { } until
                    ? Math.Max(0, (long)(until.ToUniversalTime() - DateTime.UtcNow).TotalSeconds)
                    : 0;

            if (initSec > 0)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (RemainingSeconds > 0)
                    {
                        return;
                    }

                    RemainingSeconds = initSec;
                    UpdateBalanceUi();
                });
            }
        }

        if (Interlocked.CompareExchange(ref _isSyncingBalance, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var (success, profile, error) = await _apiService.GetProfileAsync();
            if (success && profile is not null)
            {
                session.SubscriptionUntil = profile.SubscriptionUntil;
                session.BalanceSeconds = profile.BalanceSeconds;
                await AuthManager.SaveSessionAsync(session);
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    RemainingSeconds = profile.BalanceSeconds > 0
                        ? profile.BalanceSeconds
                        : profile.SubscriptionUntil is { } until
                            ? Math.Max(0, (long)(until.ToUniversalTime() - DateTime.UtcNow).TotalSeconds)
                            : 0;

                    UpdateBalanceUi();
                    _tabContentVpn?.UpdateBalanceUi(
                        TimeFormatHelper.FormatSeconds(RemainingSeconds, false),
                        VpnView.FormatBytes(profile.TotalBytesUsed));
                });
            }
            else
            {
                AppLogger.LogWarning($"[SYNC ERROR] {error}");
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    var fallbackSec = session.BalanceSeconds > 0
                        ? session.BalanceSeconds
                        : session.SubscriptionUntil is { } until && until > DateTime.UtcNow
                            ? Math.Max(0, (long)(until.ToUniversalTime() - DateTime.UtcNow).TotalSeconds)
                            : 0;
                    if (fallbackSec <= 0 || RemainingSeconds > 0)
                    {
                        return;
                    }

                    RemainingSeconds = fallbackSec;
                    UpdateBalanceUi();
                });

                if (retryCount < 3)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay((retryCount + 1) * 2500);
                        await SyncBalanceFromServerAsync(session, retryCount + 1);
                    });
                }
            }
        }
        finally
        {
            _ = Interlocked.Exchange(ref _isSyncingBalance, 0);
        }
    }

    private async Task ConsumeTimeLoopAsync(CancellationToken ct)
    {
        var syncCounter = 0;
        while (!ct.IsCancellationRequested && RemainingSeconds > 0)
        {
            try
            {
                await Task.Delay(1000, ct);
                RemainingSeconds = Math.Max(0, RemainingSeconds - 1);
                PlatformThrottler.Post("ui:balance", TimeSpan.FromMilliseconds(250), UpdateBalanceUi);

                syncCounter++;
                if (syncCounter < 30)
                {
                    continue;
                }

                syncCounter = 0;
                _ = SyncBalanceFromServerAsync();
            }
            catch
            {
                break;
            }
        }

        if (RemainingSeconds <= 0)
        {
            await VpnService.StopVpnAsync();
            await MainThread.InvokeOnMainThreadAsync(() =>
                DisplayAlertAsync("Лимит", "Время действия тарифа закончилось. Пополните баланс.", "ОК"));
        }
    }

    //управление затемнением сайдбара при открытии модальных окон
    public async Task SetSidebarDimmedAsync(bool dimmed)
    {
        if (SidebarDimmer is null)
        {
            return;
        }

        if (dimmed)
        {
            SidebarDimmer.IsVisible = true;
            SidebarDimmer.InputTransparent = false;
            _ = await SidebarDimmer.FadeToAsync(1.0, 180, Easing.CubicOut);
        }
        else
        {
            SidebarDimmer.InputTransparent = true;
            _ = await SidebarDimmer.FadeToAsync(0.0, 150, Easing.CubicIn);
            SidebarDimmer.IsVisible = false;
        }
    }

    //клик по затемненному сайдбару для закрытия активной модалки
    private void OnSidebarDimmerTapped(object? sender, EventArgs e)
    {
        ThemeAudioService.PlayClick();
        if (_tabContentVpn?.IsServerModalOpen == true)
        {
            _ = _tabContentVpn.CloseServerModalAsync();
        }

        if (_tabContentThemeStore?.IsPreviewModalOpen == true)
        {
            _ = _tabContentThemeStore.ClosePreviewModalAsync();
        }
    }

    public Task<bool> ShowCustomDialogAsync(
        string title,
        string message,
        string? acceptText = null,
        string cancelText = "OK") =>
        NeoDialog.ShowAsync(title, message, acceptText, cancelText);

    public new Task<bool> DisplayAlert(string title, string message, string accept, string cancel) =>
        ShowCustomDialogAsync(title, message, accept, cancel);

    public new Task DisplayAlert(string title, string message, string cancel) =>
        ShowCustomDialogAsync(title, message, null, cancel);

    public new Task<bool> DisplayAlert(string title, string message, string accept, string cancel, FlowDirection flowDirection)
    {
        _ = flowDirection;
        return ShowCustomDialogAsync(title, message, accept, cancel);
    }

    public new Task DisplayAlert(string title, string message, string cancel, FlowDirection flowDirection)
    {
        _ = flowDirection;
        return ShowCustomDialogAsync(title, message, null, cancel);
    }

    public new Task<bool> DisplayAlertAsync(string title, string message, string accept, string cancel,
        FlowDirection flowDirection = FlowDirection.MatchParent)
    {
        _ = flowDirection;
        return ShowCustomDialogAsync(title, message, accept, cancel);
    }

    public new Task DisplayAlertAsync(string title, string message, string cancel, FlowDirection flowDirection = FlowDirection.MatchParent)
    {
        _ = flowDirection;
        return ShowCustomDialogAsync(title, message, null, cancel);
    }
}
