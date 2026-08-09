namespace obxodka.Views;

public partial class VpnView : ContentView
{
    private MainPage _parent = null!;
    private IVpnService _vpnService = null!;
    private ApiService _apiService = null!;
    private bool _isBusy;
    private bool _isErrorState;
    private double _graphWidth = 200;
    private double _graphHeight = 60;
    private float _loaderAngle;
    private IDispatcherTimer? _loaderTimer;

    private readonly List<double> _graphHistoryUp = [.. new double[20]];
    private readonly List<double> _graphHistoryDown = [.. new double[20]];
    private long _lastBytesSent, _lastBytesReceived;
    private DateTime _lastTrafficUpdate = DateTime.Now;

    private static readonly string[] t_suffixes = ["B", "KB", "MB", "GB", "TB"];

    public VpnView() => InitializeComponent();

    public void Initialize(MainPage parent, IVpnService vpnService, ApiService apiService)
    {
        UpdateRayIndicator();
        _parent = parent;
        _vpnService = vpnService;
        _apiService = apiService;

        _vpnService.OnStateChanged -= HandleAppVpnStateChanged;
        _vpnService.OnErrorOccurred -= HandleVpnError;
        _vpnService.OnLogUpdated -= HandleVpnLog;
        _vpnService.OnStateChanged += HandleAppVpnStateChanged;
        _vpnService.OnErrorOccurred += HandleVpnError;
        _vpnService.OnLogUpdated += HandleVpnLog;

        OctopusEngine.Current.OnTrafficUpdated -= OnTrafficUpdated;
        OctopusEngine.Current.OnTrafficUpdated += OnTrafficUpdated;
    }
    public void UnsubscribeEvents()
    {
        if (_vpnService != null)
        {
            _vpnService.OnStateChanged -= HandleAppVpnStateChanged;
            _vpnService.OnErrorOccurred -= HandleVpnError;
            _vpnService.OnLogUpdated -= HandleVpnLog;
        }
        OctopusEngine.Current.OnTrafficUpdated -= OnTrafficUpdated;
    }

    public async Task PlayEntranceAnimationAsync()
    {
        UpdateRayIndicator();
        await UIAnimations.PlayEntranceCascadeAsync(80, 450,
            CardIpWrapper, Card2Wrapper, Card5Wrapper, Card1Wrapper);
    }

    public void UpdateBalanceUI(string timeText, string? trafficText = null)
    {
        TokenAmountLabel.Text = timeText;
        if (trafficText != null)
        {
            TotalTrafficLabelMain.Text = trafficText;
        }
    }

    private void UpdateRayIndicator()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var rays = Preferences.Get("BatteryMode", 2);
            if (OctopusEngine.Current != null && OctopusEngine.Current.IsConnected)
            {
                rays = OctopusEngine.Current.ActiveRays;
            }

            var useHttp3 = Preferences.Get("UseHttp3", false);
            var protoText = useHttp3 ? "HTTP/3" : "HTTP/2";

            if (rays == 1)
            {
                RayIndicatorIcon.Icon = FluentIcons.LeafOne24;
                RayIndicatorIcon.IconColor = Color.FromArgb("#10B981");
                RayIndicatorLabel.Text = $"Режим: Eco (1 Луч / {protoText})";
            }
            else if (rays == 8)
            {
                RayIndicatorIcon.Icon = FluentIcons.Flash24;
                RayIndicatorIcon.IconColor = Color.FromArgb("#EF4444");
                RayIndicatorLabel.Text = $"Режим: Турбо (8 Лучей / {protoText})";
            }
            else
            {
                RayIndicatorIcon.Icon = FluentIcons.Scales24;
                RayIndicatorIcon.IconColor = Color.FromArgb("#3B82F6");
                RayIndicatorLabel.Text = $"Режим: Баланс (2 Луча / {protoText})";
            }
        });
    }

    private void HandleVpnLog(string logMsg) =>
        MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = logMsg);
    private void HandleVpnError(string err) =>
        MainThread.BeginInvokeOnMainThread(async () =>
            await _parent.DisplayAlertAsync("Сбой сети", err, "OK"));

    private void HandleAppVpnStateChanged(AppVpnState state)
    {
        UpdateRayIndicator();
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            switch (state)
            {
                case AppVpnState.Disconnected:
                    _isBusy = false;
                    _isErrorState = false;
                    StopLoaderAnimation();
                    OuterAura.IsVisible = true;
                    IpAddressLabel.Text = "IP: не назначен";
                    ConnectButtonCore.IsEnabled = true;
                    await SetNeonStateAsync("Не в сети", "СТАРТ", false);
                    _parent.NotifyVpnDisconnected();
                    break;

                case AppVpnState.Connected:
                    _isBusy = false;
                    _isErrorState = false;
                    StartLoaderAnimation();
                    OuterAura.IsVisible = true;
                    IpAddressLabel.Text = $"IP: {OctopusEngine.Current?.AssignedIp}";
                    ConnectButtonCore.IsEnabled = true;
                    await SetNeonStateAsync("Защищено", "СТОП", true);
                    _parent.NotifyVpnConnected();
                    break;

                case AppVpnState.Error:
                    _isBusy = false;
                    _isErrorState = true;
                    StopLoaderAnimation();
                    OuterAura.IsVisible = true;
                    IpAddressLabel.Text = "IP: не назначен";
                    ConnectButtonCore.IsEnabled = true;
                    await SetNeonStateAsync("Ошибка", "ПОВТОРИТЬ", false, true);
                    _parent.NotifyVpnDisconnected();
                    break;

                case AppVpnState.Connecting:
                case AppVpnState.Reconnecting:
                    _isErrorState = false;
                    StartLoaderAnimation();
                    ConnectButtonCore.IsEnabled = false;
                    IpAddressLabel.Text = "IP: получение...";
                    await SetNeonStateAsync("Подключение...", "ЖДИТЕ", false);
                    break;

                case AppVpnState.Disconnecting:
                    ConnectButtonCore.IsEnabled = false;
                    IpAddressLabel.Text = "IP: отключение...";
                    StatusLabel.Text = "Отключение...";
                    ConnectButtonText.Text = "ЖДИТЕ";
                    _ = LoaderCanvas.ScaleToAsync(1.0, 500, Easing.SpringOut);
                    _ = LoaderCanvas.FadeToAsync(0, 400, Easing.CubicOut);
                    break;
                default:
                    break;
            }
        });
    }

    private async Task SetNeonStateAsync(string status, string btnText, bool connected, bool isError = false)
    {
        StatusLabel.Text = status;
        ConnectButtonText.Text = btnText;

        if (connected)
        {
            await UIAnimations.SetVpnConnectedAsync(ConnectIcon, StatusLabel, OuterAura);
            ConnectButtonCore.BackgroundColor = Color.FromArgb("#1A00E5FF");
            ConnectButtonCore.Stroke = Color.FromArgb("#00E5FF");
            var targetScale = DeviceInfo.Idiom == DeviceIdiom.Phone ? 1.3 : 2.0;
            _ = LoaderCanvas.ScaleToAsync(targetScale, 500, Easing.SpringOut);
        }
        else if (isError)
        {
            await UIAnimations.SetVpnDisconnectedAsync(ConnectIcon, StatusLabel, OuterAura);
            ConnectIcon.IconColor = Colors.Red;
            StatusLabel.TextColor = Colors.Red;
            ConnectButtonCore.BackgroundColor = Color.FromArgb("#1AFF0000");
            ConnectButtonCore.Stroke = Colors.Red;
            _ = LoaderCanvas.ScaleToAsync(1.0, 500, Easing.SpringOut);
        }
        else
        {
            await UIAnimations.SetVpnDisconnectedAsync(ConnectIcon, StatusLabel, OuterAura);
            ConnectButtonCore.ClearValue(BackgroundColorProperty);
            ConnectButtonCore.ClearValue(Border.StrokeProperty);
            _ = LoaderCanvas.ScaleToAsync(1.0, 500, Easing.SpringOut);
        }
    }

    private async void OnConnectClickedAsync(object? sender, EventArgs? e)
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;

        if (_vpnService.CurrentState is not (AppVpnState.Disconnected or AppVpnState.Error))
        {
            _ = ConnectButtonCore.BounceClickAsync();
            _ = Task.Run(async () =>
            {
                try
                {
                    _ = _apiService.StopVpnOnServerAsync();
                    await _vpnService.StopVpnAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[VPN STOP ERROR] {ex.Message}");
                }
            });
            return;
        }

        await ConnectButtonCore.BounceClickAsync();
        try
        {
            if (_parent.RemainingSeconds <= 0)
            {
                await _parent.DisplayAlertAsync("Внимание", "Нет доступного времени.", "OK");
                return;
            }
            var (success, servers, _) = await _apiService.GetServersAsync();
            if (!success || servers == null || servers.Count == 0)
            {
                await _parent.DisplayAlertAsync("Ошибка", "Не удалось получить список нod", "OK");
                return;
            }

            var (successHash, hashData, _) = await _apiService.GetCertHashAsync();
            if (successHash && hashData != null && !string.IsNullOrEmpty(hashData.Hash))
            {
                OctopusEngine.DynamicSslPublicKeyHash = hashData.Hash;
            }

            await Task.Run(async () => await _vpnService.StartVpnAsync(servers[0].Ip, servers[0].Port));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VPN CONNECT ERROR] {ex.Message}");
            await _parent.DisplayAlertAsync("Ошибка", "Произошла ошибка при подключении/отключении", "OK");
        }
        finally
        {
            _isBusy = false;
        }
    }
    private void OnGraphContainerSizeChanged(object? sender, EventArgs e)
    {
        if (GraphContainer.Width > 0 && GraphContainer.Height > 0)
        {
            _graphWidth = GraphContainer.Width;
            _graphHeight = GraphContainer.Height;
        }
    }

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

            var maxOverall = Math.Max(
                Math.Max(_graphHistoryUp.Max(), _graphHistoryDown.Max()), 1024);

            var ptUp = new PointCollection { new Point(0, _graphHeight) };
            var ptDown = new PointCollection { new Point(0, _graphHeight) };
            for (var i = 0; i < _graphHistoryUp.Count; i++)
            {
                var x = i * (_graphWidth / 19.0);
                var paddingY = _graphHeight * 0.1;
                var usableHeight = _graphHeight - paddingY;
                ptUp.Add(new Point(x, _graphHeight - (_graphHistoryUp[i] / maxOverall * usableHeight)));
                ptDown.Add(new Point(x, _graphHeight - (_graphHistoryDown[i] / maxOverall * usableHeight)));
            }
            ptUp.Add(new Point(_graphWidth, _graphHeight));
            ptDown.Add(new Point(_graphWidth, _graphHeight));
            TrafficGraphUp.Points = ptUp;
            TrafficGraphDown.Points = ptDown;
        });
    }

    public static string FormatBytes(double bytes)
    {
        int i;
        var d = bytes;
        for (i = 0; i < t_suffixes.Length && bytes >= 1024; i++, bytes /= 1024)
        {
            d = bytes / 1024.0;
        }

        return $"{d:0.##} {t_suffixes[i]}";
    }
    private void StartLoaderAnimation()
    {
        if (_loaderTimer is not null)
        {
            return;
        }

        _loaderTimer = Dispatcher.CreateTimer();
        _loaderTimer.Interval = TimeSpan.FromMilliseconds(16);
        _loaderTimer.Tick += (_, _) =>
        {
            _loaderAngle += 1.5f;
            LoaderCanvas?.InvalidateSurface();
        };
        LoaderCanvas.Opacity = 1;
        LoaderCanvas.IsVisible = true;
        _loaderTimer.Start();
    }

    private void StopLoaderAnimation()
    {
        if (LoaderCanvas != null)
        {
            _ = LoaderCanvas.ScaleToAsync(1.0, 300, Easing.SpringOut);
            _ = LoaderCanvas.FadeToAsync(0, 300, Easing.CubicOut).ContinueWith(t => MainThread.BeginInvokeOnMainThread(() =>
                {
                    _loaderTimer?.Stop();
                    _loaderTimer = null;
                    LoaderCanvas.IsVisible = false;
                }));
        }
        else
        {
            _loaderTimer?.Stop();
            _loaderTimer = null;
        }
    }

    private void OnPaintLoaderSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        if (_loaderTimer is null)
        {
            return;
        }

        var cx = e.Info.Width / 2f;
        var cy = e.Info.Height / 2f;
        var r = Math.Min(cx, cy) - 4f;

        var colorPurple = _isErrorState ? SKColors.DarkRed : SKColor.Parse("#7C3AED");
        var colorCyan = _isErrorState ? SKColors.Red : SKColor.Parse("#00E5FF");

        using var glowPaint = new SKPaint
        {
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 22f),
            Color = colorPurple.WithAlpha(45)
        };
        canvas.DrawCircle(cx, cy, r - 30f, glowPaint);

        var configs = new (float speed, float delay, float ox, float oy, float size, int sides)[]
        {
            ( 1.0f,   0f, 0.5f, 0.5f, 0.74f, 5),
            (-1.0f,   0f, 0.5f, 0.5f, 0.65f, 6),
            ( 1.5f,  60f, 0.5f, 0.6f, 0.54f, 5),
            (-1.5f, -60f, 0.4f, 0.4f, 0.45f, 4),
            ( 2.0f, 120f, 0.6f, 0.4f, 0.38f, 6),
        };

        using var polyPaint = new SKPaint { IsAntialias = true };
        for (var i = 0; i < configs.Length; i++)
        {
            var (speed, delay, ox, oy, size, sides) = configs[i];
            var rot = (_loaderAngle * speed) + delay;
            var alpha = (byte)(90 + (i * 15));
            polyPaint.Color = (i % 2 == 0 ? colorPurple : colorCyan).WithAlpha(alpha);
            polyPaint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 8f);

            var pivotX = cx + ((ox - 0.5f) * r);
            var pivotY = cy + ((oy - 0.5f) * r);
            _ = canvas.Save();
            canvas.RotateDegrees(rot, pivotX, pivotY);
            var path = MakePolygon(pivotX, pivotY, r * size, sides);
            canvas.DrawPath(path, polyPaint);
            canvas.Restore();
        }

    }

#pragma warning disable CS0618
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
#pragma warning restore CS0618
}
