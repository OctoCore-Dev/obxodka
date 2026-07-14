namespace obxodka.Pages;

internal sealed partial class MainPage : ContentPage, IDisposable
{
    private readonly IVpnService _vpnService;
    private readonly ApiService _apiService;
    private bool _isBusy;
    private CancellationTokenSource? _vpnCts;
    public long RemainingSeconds { get; private set; }
    public bool IsVpnRunning => _vpnService.IsRunning;
    public ObservableCollection<DeviceItem> ConnectedDevices { get; } = [];
    private List<AppInfoItem> _allApps = [];
    private readonly ObservableCollection<AppInfoItem> _displayedApps = [];
    public bool IsSplitEditingAllowed { get; set; }
    private bool _isOldPassVisible;
    private bool _isNewPassVisible;
    private string _activeTab = "auth";
    public MainPage(IVpnService vpnService, ApiService apiService)
    {
        InitializeComponent();
        _vpnService = vpnService;
        _apiService = apiService;
        DevicesList.ItemsSource = ConnectedDevices;
        SplitAppsList.ItemsSource = _displayedApps;
        BindingContext = this;
    }

    public void Dispose()
    {
        _vpnCts?.Cancel();
        _vpnCts?.Dispose();
        _vpnCts = null;
        GC.SuppressFinalize(this);
    }

    private void OnPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var info = e.Info;

        var isDark = Application.Current?.RequestedTheme == AppTheme.Dark;
        canvas.Clear(isDark ? new SKColor(5, 5, 7) : new SKColor(240, 242, 245));

        var center1 = new SKPoint(info.Width * 0.2f, info.Height * 0.8f);
        var center2 = new SKPoint(info.Width * 0.8f, info.Height * 0.2f);

        var colors1 = new[] { new SKColor(139, 92, 246, isDark ? (byte)35 : (byte)20), SKColors.Transparent };
        var colors2 = new[] { new SKColor(0, 240, 255, isDark ? (byte)45 : (byte)25), SKColors.Transparent };

        using var shader1 = SKShader.CreateRadialGradient(center1, 600, colors1, null, SKShaderTileMode.Clamp);
        using var shader2 = SKShader.CreateRadialGradient(center2, 800, colors2, null, SKShaderTileMode.Clamp);

        using var paint1 = new SKPaint { Shader = shader1 };
        using var paint2 = new SKPaint { Shader = shader2 };

        canvas.DrawRect(info.Rect, paint1);
        canvas.DrawRect(info.Rect, paint2);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        _vpnService.OnStateChanged -= HandleAppVpnStateChanged;
        _vpnService.OnErrorOccurred -= HandleVpnError;
        _vpnService.OnLogUpdated -= HandleVpnLog;
        _vpnService.OnStateChanged += HandleAppVpnStateChanged;
        _vpnService.OnErrorOccurred += HandleVpnError;
        _vpnService.OnLogUpdated += HandleVpnLog;
        OctopusEngine.Current.OnTrafficUpdated -= OnTrafficUpdated;
        OctopusEngine.Current.OnTrafficUpdated += OnTrafficUpdated;

        var session = await AuthManager.LoadSessionAsync();
        if (session is { IsLoggedIn: true, JwtToken: not null })
        {
            await SwitchToAppAfterAuthAsync();
        }
        else
        {
            DesktopSidebar.IsVisible = false;
            MobileBottomBar.IsVisible = false;
            SwitchTab("auth");
            _ = UIAnimations.PlayEntranceCascadeAsync(100, 600, AppIconLeft, FormContainer);
            _ = UIAnimations.PlayEntranceCascadeAsync(100, 600, AppIconLogin, TitleLabelLogin, LoginButtonBorder, SwitchToRegisterLabel);
        }

        if (App.PendingTileAction && session.IsLoggedIn)
        {
            App.PendingTileAction = false;

            if (!_isBusy)
            {
                OnConnectClickedAsync(this, EventArgs.Empty);
            }

            return;
        }
        HandleAppVpnStateChanged(_vpnService.CurrentState);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vpnService.OnStateChanged -= HandleAppVpnStateChanged;
        _vpnService.OnErrorOccurred -= HandleVpnError;
        _vpnService.OnLogUpdated -= HandleVpnLog;
        OctopusEngine.Current.OnTrafficUpdated -= OnTrafficUpdated;
    }
    private void OnNavVpnTapped(object? s, EventArgs e) => SwitchTab("vpn");
    private void OnNavProfileTapped(object? s, EventArgs e) => SwitchTab("profile");
    private void OnNavDevicesTapped(object? s, EventArgs e) => SwitchTab("devices");
    private void OnNavPasswordTapped(object? s, EventArgs e) => SwitchTab("password");
    private void OnNavSplitTapped(object? s, EventArgs e) => SwitchTab("split");

    private void SwitchTab(string tab)
    {
        if (_activeTab == tab && tab != "vpn" && tab != "auth")
        {
            return;
        }

        _activeTab = tab;

        TabContentAuth.IsVisible = false;
        TabContentVpn.IsVisible = false;
        TabContentProfile.IsVisible = false;
        TabContentDevices.IsVisible = false;
        TabContentPassword.IsVisible = false;
        TabContentPayment.IsVisible = false;
        TabContentSplit.IsVisible = false;
        TabContentDelete.IsVisible = false;

        SetNavInactive(BottomNavVpnIcon, SideNavVpn);
        SetNavInactive(BottomNavProfileIcon, SideNavProfile);
        SetNavInactive(BottomNavDevicesIcon, SideNavDevices);
        SetNavInactive(BottomNavPasswordIcon, SideNavPassword);
        SetNavInactive(BottomNavSplitIcon, SideNavSplit);

        switch (tab)
        {
            case "auth":
                TabContentAuth.IsVisible = true;
                break;
            case "vpn":
                TabContentVpn.IsVisible = true;
                SetNavActive(BottomNavVpnIcon, SideNavVpn, "Primary");
                _ = UIAnimations.PlayEntranceCascadeAsync(80, 500, Card1Wrapper, Card2Wrapper, Card3Wrapper, Card5Wrapper);
                break;
            case "profile":
                TabContentProfile.IsVisible = true;
                SetNavActive(BottomNavProfileIcon, SideNavProfile, "Primary");
                LoadProfileTabDataAsync(null);
                break;
            case "devices":
                TabContentDevices.IsVisible = true;
                SetNavActive(BottomNavDevicesIcon, SideNavDevices, "Primary");
                _ = LoadDevicesAsync();
                break;
            case "password":
                TabContentPassword.IsVisible = true;
                SetNavActive(BottomNavPasswordIcon, SideNavPassword, "Primary");
                break;
            case "payment":
                TabContentPayment.IsVisible = true;
                break;
            case "split":
                TabContentSplit.IsVisible = true;
                SetNavActive(BottomNavSplitIcon, SideNavSplit, "Primary");
                _ = LoadSplitAppsAsync();
                break;
            case "delete":
                TabContentDelete.IsVisible = true;
                break;
            default:
                break;
        }
    }

    private static Color GetThemeColor(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var val) == true && val is Color c ? c : Colors.Gray;

    private static void SetNavActive(MauiIcons.Core.MauiIcon? bottomIcon, Border? sideItem, string colorKey)
    {
        var c = GetThemeColor(colorKey);

        _ = bottomIcon?.IconColor = c;
        _ = sideItem?.BackgroundColor = Application.Current?.RequestedTheme == AppTheme.Dark
                ? Color.FromArgb("#334155")
                : Colors.White;
    }

    private static void SetNavInactive(MauiIcons.Core.MauiIcon? bottomIcon, Border? sideItem)
    {
        var gray = GetThemeColor("Gray500");

        _ = bottomIcon?.IconColor = gray;
        _ = sideItem?.BackgroundColor = Colors.Transparent;
    }

    private void HandleVpnLog(string logMsg) =>
        MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = logMsg);

    private void HandleVpnError(string err) =>
        MainThread.BeginInvokeOnMainThread(async () => await DisplayAlertAsync("Сбой сети", err, "OK"));

    private void SetNeonState(string status, string btnText)
    {
        var state = _vpnService.CurrentState;
        StatusLabel.Text = status;
        ConnectButtonText.Text = btnText;

        if (state is AppVpnState.Connecting or AppVpnState.Reconnecting or AppVpnState.Connected)
        {
            _ = OuterAura.FadeToAsync(0, 300);
            _ = this.AbortAnimation("AuraPulse");
        }
        else
        {
            _ = this.AbortAnimation("AuraPulse");
            ConnectIcon.IconColor = GetThemeColor("Primary");
            ConnectButtonText.TextColor = GetThemeColor("Primary");
            _ = OuterAura.ScaleToAsync(0.8, 500, Easing.SpringIn);
            _ = OuterAura.FadeToAsync(0, 300);
        }

        if (state == AppVpnState.Connected)
        {
            ConnectIcon.IconColor = GetThemeColor("Secondary");
            ConnectButtonText.TextColor = GetThemeColor("Secondary");
            _ = OuterAura.ScaleToAsync(1.0, 800, Easing.SpringOut);
            _ = OuterAura.FadeToAsync(0.6, 400);

            _ = this.AbortAnimation("AuraPulse");
            var pulse = new Animation(v => OuterAura.Scale = v, 1.0, 1.12);
            pulse.Commit(this, "AuraPulse", 16, 2000, Easing.SinInOut, null, () => true);
        }
    }

    private void HandleAppVpnStateChanged(AppVpnState state)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            switch (state)
            {
                case AppVpnState.Disconnected:
                    StopLoaderAnimation();
                    OuterAura.IsVisible = true;
                    SetNeonState("Не в сети", "СТАРТ");
                    _vpnCts?.Cancel();
                    _vpnCts = null;
                    break;
                case AppVpnState.Connected:
                    OuterAura.IsVisible = false;
                    SetNeonState("Защищено", "СТОП");
                    if (_vpnCts == null && RemainingSeconds > 0)
                    {
                        _vpnCts = new CancellationTokenSource();
                        _ = ConsumeTimeLoopAsync(_vpnCts.Token);
                    }
                    break;
                case AppVpnState.Error:
                    StopLoaderAnimation();
                    OuterAura.IsVisible = true;
                    SetNeonState("Ошибка соединения", "ПОВТОРИТЬ");
                    _vpnCts?.Cancel();
                    _vpnCts = null;
                    break;
                case AppVpnState.Connecting:
                case AppVpnState.Reconnecting:
                    StartLoaderAnimation();
                    OuterAura.IsVisible = false;
                    SetNeonState("Подключение...", "СТОП");
                    break;
                case AppVpnState.Disconnecting:
                    break;
                default:
                    break;
            }
        });
    }

    private async void OnConnectClickedAsync(object? sender, EventArgs? e)
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        try
        {
            if (_vpnService.CurrentState is AppVpnState.Disconnected or AppVpnState.Error)
            {
                if (RemainingSeconds <= 0)
                {
                    await DisplayAlertAsync("Внимание", "Нет доступного времени.", "OK");
                    return;
                }
                var (success, servers, _) = await _apiService.GetServersAsync();
                if (!success || servers == null || servers.Count == 0)
                {
                    await DisplayAlertAsync("Ошибка", "Не удалось получить список нод", "OK");
                    return;
                }
                var server = servers[0];
                await _vpnService.StartVpnAsync(server.Ip, server.Port);
            }
            else
            {
                _ = _apiService.StopVpnOnServerAsync();
                await _vpnService.StopVpnAsync();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VPN CONNECT ERROR] {ex.Message}");
            await DisplayAlertAsync("Ошибка", "Произошла ошибка при подключении/отключении", "OK");
        }
        finally
        {
            _isBusy = false;
        }
    }

    private void UpdateBalanceUI()
    {
        var hours = RemainingSeconds / 3600;
        var minutes = RemainingSeconds % 3600 / 60;
        TokenAmountLabel.Text = $"{hours}ч {minutes:D2}м";
        ProfileTokenLabel.Text = $"{hours}ч {minutes:D2}м";
    }

    private async Task SyncBalanceFromServerAsync(UserSession session)
    {
        var (success, profile, _) = await _apiService.GetProfileAsync();
        if (success && profile != null)
        {
            session.SubscriptionUntil = profile.SubscriptionUntil;
            await AuthManager.SaveSessionAsync(session);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                RemainingSeconds = profile.SubscriptionUntil.HasValue 
                    ? Math.Max(0, (long)(profile.SubscriptionUntil.Value - DateTime.UtcNow).TotalSeconds)
                    : 0;
                UpdateBalanceUI();
                TotalTrafficLabelMain.Text = FormatBytes(profile.TotalBytesUsed);
            });
        }
    }

    private async Task ConsumeTimeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && RemainingSeconds > 0)
        {
            try
            {
                await Task.Delay(1000, ct);
                RemainingSeconds--;
                MainThread.BeginInvokeOnMainThread(UpdateBalanceUI);
            }
            catch { break; }
        }
        if (RemainingSeconds <= 0)
        {
            await _vpnService.StopVpnAsync();
            await MainThread.InvokeOnMainThreadAsync(() =>
                DisplayAlertAsync("Лимит", "Пополните баланс", "ОК"));
        }
    }

    private readonly List<double> _graphHistoryUp = [.. new double[20]];
    private readonly List<double> _graphHistoryDown = [.. new double[20]];
    private long _lastBytesSent, _lastBytesReceived;
    private DateTime _lastTrafficUpdate = DateTime.Now;

    private void OnTrafficUpdated(long bytesSent, long bytesReceived)
    {
        var now = DateTime.Now;
        var elapsed = (now - _lastTrafficUpdate).TotalSeconds;

        if (elapsed < 1.0)
        {
            return;
        }

        var sentSpeed = (bytesSent - _lastBytesSent) / elapsed;
        var recvSpeed = (bytesReceived - _lastBytesReceived) / elapsed;
        _lastBytesSent = bytesSent;
        _lastBytesReceived = bytesReceived;
        _lastTrafficUpdate = now;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            TrafficUpLabel.Text = $"{FormatBytes(sentSpeed)}/s";
            TrafficDownLabel.Text = $"{FormatBytes(recvSpeed)}/s";

            _graphHistoryUp.Add(sentSpeed);
            _graphHistoryDown.Add(recvSpeed);
            if (_graphHistoryUp.Count > 20)
            {
                _graphHistoryUp.RemoveAt(0);
            }

            if (_graphHistoryDown.Count > 20)
            {
                _graphHistoryDown.RemoveAt(0);
            }

            var maxOverall = Math.Max(Math.Max(_graphHistoryUp.Max(), _graphHistoryDown.Max()), 1024);

            var pointsUp = new PointCollection { new Point(0, 60) };
            var pointsDown = new PointCollection { new Point(0, 60) };
            for (var i = 0; i < _graphHistoryUp.Count; i++)
            {
                var x = i * (200.0 / 19.0);
                pointsUp.Add(new Point(x, 60 - (_graphHistoryUp[i] / maxOverall * 50)));
                pointsDown.Add(new Point(x, 60 - (_graphHistoryDown[i] / maxOverall * 50)));
            }
            pointsUp.Add(new Point(200, 60));
            pointsDown.Add(new Point(200, 60));
            TrafficGraphUp.Points = pointsUp;
            TrafficGraphDown.Points = pointsDown;
        });
    }

    private static readonly string[] t_suffixes = ["B", "KB", "MB", "GB", "TB"];

    private static string FormatBytes(double bytes)
    {
        int i;
        var dblSByte = bytes;
        for (i = 0; i < t_suffixes.Length && bytes >= 1024; i++, bytes /= 1024)
        {
            dblSByte = bytes / 1024.0;
        }

        return $"{dblSByte:0.##} {t_suffixes[i]}";
    }

    private float _loaderAngle;
    private IDispatcherTimer? _loaderTimer;

    private void StartLoaderAnimation()
    {
        if (_loaderTimer != null)
        {
            return;
        }

        _loaderTimer = Dispatcher.CreateTimer();
        _loaderTimer.Interval = TimeSpan.FromMilliseconds(16);
        _loaderTimer.Tick += (s, e) =>
        {
            _loaderAngle += 1.5f;
            LoaderCanvas?.InvalidateSurface();
        };
        _loaderTimer.Start();
    }

    private void StopLoaderAnimation()
    {
        _loaderTimer?.Stop();
        _loaderTimer = null;
        LoaderCanvas?.InvalidateSurface();
    }

    private void OnPaintLoaderSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        if (_loaderTimer == null)
        {
            return;
        }

        var cx = e.Info.Width / 2f;
        var cy = e.Info.Height / 2f;
        var r = Math.Min(cx, cy) - 4f;

        var colorPurple = SKColor.Parse("#8B5CF6");
        var colorCyan = SKColor.Parse("#00F0FF");

        using var glowPaint = new SKPaint
        {
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 22f),
            Color = colorPurple.WithAlpha(50)
        };
        canvas.DrawCircle(cx, cy, r, glowPaint);

        var configs = new (float speed, float delay, float ox, float oy, float size, int sides)[]
        {
            ( 1.0f,   0f, 0.5f, 0.5f, 0.42f, 5),
            (-1.0f,   0f, 0.5f, 0.5f, 0.38f, 6),
            ( 1.5f,  60f, 0.5f, 0.6f, 0.30f, 5),
            (-1.5f, -60f, 0.4f, 0.4f, 0.28f, 4),
            ( 2.0f, 120f, 0.6f, 0.4f, 0.25f, 6),
        };

        using var polyPaint = new SKPaint { IsAntialias = true };

        for (var i = 0; i < configs.Length; i++)
        {
            var (speed, delay, ox, oy, size, sides) = configs[i];
            var rot = (_loaderAngle * speed) + delay;
            var alpha = 90f + (i * 15f);

            polyPaint.Color = (i % 2 == 0 ? colorPurple : colorCyan).WithAlpha((byte)alpha);
            polyPaint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 8f);

            var pivotX = cx + ((ox - 0.5f) * r);
            var pivotY = cy + ((oy - 0.5f) * r);

            _ = canvas.Save();
            canvas.RotateDegrees(rot, pivotX, pivotY);

            var path = MakePolygon(pivotX, pivotY, r * size, sides);
            canvas.DrawPath(path, polyPaint);
            canvas.Restore();
        }

        using var ringPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            Color = colorCyan.WithAlpha(60)
        };
        canvas.DrawCircle(cx, cy, r - 1f, ringPaint);
    }

    private static SKPath MakePolygon(float cx, float cy, float r, int sides)
    {
        var path = new SKPath();
        for (var i = 0; i < sides; i++)
        {
            var a = (float)((i * 2 * Math.PI / sides) - (Math.PI / 2));
            var x = cx + (r * MathF.Cos(a));
            var y = cy + (r * MathF.Sin(a));
            if (i == 0)
            {
                path.MoveTo(x, y);
            }
            else
            {
                path.LineTo(x, y);
            }
        }
        path.Close();
        return path;
    }
}
