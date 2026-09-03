namespace obxodka.Views;

public sealed partial class MeshView : ContentView
{
    private readonly bool _isLoaded;
    private IDispatcherTimer? _metricsTimer;
    private ApiService? _apiService;
    private string _currentMyCode = string.Empty;
    private const long RewardThresholdBytes = 5L * 1024 * 1024 * 1024;

    public MeshView()
    {
        InitializeComponent();

        MeshScrollView?.SizeChanged += (s, e) =>
            {
                if (MeshScrollView.Width > 0)
                {
                    ApplyCardWidth(MeshScrollView.Width);
                }
            };

        LoadSettings();
        _isLoaded = true;

        Unloaded += (_, _) => StopMetricsTimer();
    }

    public void ForceLayoutWidth()
    {
        if (MeshScrollView.Width > 0)
        {
            ApplyCardWidth(MeshScrollView.Width);
        }
        else if (Width > 0 && RootLayout is not null)
        {
            var avail = Width - RootLayout.Padding.HorizontalThickness;
            if (avail > 0)
            {
                ApplyCardWidth(avail);
            }
        }
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width > 0 && RootLayout is not null)
        {
            var availableWidth = width - RootLayout.Padding.HorizontalThickness;
            if (availableWidth > 0)
            {
                ApplyCardWidth(availableWidth);
            }
        }
    }

    private void ApplyCardWidth(double targetWidth)
    {
        if (targetWidth <= 0)
        {
            return;
        }

        var safeWidth = Math.Min(targetWidth - 6, 950);
        if (safeWidth <= 0)
        {
            return;
        }

        MeshCardsStack.WidthRequest = safeWidth;
        MeshCardsStack.MaximumWidthRequest = safeWidth;

        CardRelayServer.WidthRequest = safeWidth;
        CardReward.WidthRequest = safeWidth;
        CardLimits.WidthRequest = safeWidth;
        CardMyCode.WidthRequest = safeWidth;
        CardFriendCode.WidthRequest = safeWidth;
        CardFriendsList.WidthRequest = safeWidth;
        CardSecurity.WidthRequest = safeWidth;
    }

    public void Initialize(ApiService? apiService = null)
    {
        _apiService = apiService;
        StartMetricsTimer();
        _ = LoadReferralDataAsync();
    }

    public async Task PlayEntranceAnimationAsync()
    {
        Opacity = 1;
        TranslationY = 0;

        var cards = new VisualElement[]
        {
            CardRelayServer,
            CardReward,
            CardLimits,
            CardMyCode,
            CardFriendCode,
            CardFriendsList,
            CardSecurity
        };

        await UIAnimations.PlayEntranceCascadeAsync(80, 450, cards);
    }

    private void LoadSettings()
    {
        RelayServerSwitch.IsToggled = MeshSettings.RelayEnabled;
        SpeedLimitSlider.Value = MeshSettings.RelaySpeedMbps;
        SpeedLimitValueLabel.Text = $"{MeshSettings.RelaySpeedMbps} Мбит/с";

        var clientIndex = MaxClientsPicker.Items.IndexOf(MeshSettings.RelayMaxClients.ToString(CultureInfo.InvariantCulture));
        if (clientIndex >= 0)
        {
            MaxClientsPicker.SelectedIndex = clientIndex;
        }

        if (!string.IsNullOrWhiteSpace(MeshSettings.ReferralCode))
        {
            _currentMyCode = MeshSettings.ReferralCode;
            MyReferralCodeLabel.Text = MeshSettings.ReferralCode;
        }

        UpdateStatusBadge();
        UpdateRewardProgress(0);
    }

    public async Task LoadReferralDataAsync()
    {
        if (_apiService == null)
        {
            return;
        }

        try
        {
            var (success, data, _) = await _apiService.GetMyReferralCodeAsync();
            if (success && data != null)
            {
                _currentMyCode = data.Code;
                MeshSettings.ReferralCode = data.Code;
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    MyReferralCodeLabel.Text = data.Code;
                    FriendsCountLabel.Text = $"{data.Friends.Count} / 10";
                    RenderFriendsList(data.Friends);

                    var totalGb = data.TotalMeshBytesRelayed / (1024.0 * 1024.0 * 1024.0);
                    TotalLifetimeRelayedLabel.Text = $"Всего роздано: {totalGb:F1} ГБ";
                    UpdateRewardProgress(data.TotalMeshBytesRelayed);
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MESH VIEW] Error loading referral data: {ex.Message}");
        }
    }

    private void RenderFriendsList(List<ReferralFriendDto> friends)
    {
        FriendsListContainer.Children.Clear();

        if (friends.Count == 0)
        {
            FriendsListContainer.Children.Add(NoFriendsPlaceholder);
            return;
        }

        foreach (var friend in friends)
        {
            var item = new Border
            {
                Padding = new Thickness(14, 10),
                StrokeThickness = 0,
                BackgroundColor = Application.Current?.RequestedTheme == AppTheme.Dark
                    ? Color.FromArgb("#181824")
                    : Color.FromArgb("#F9FAFB"),
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 }
            };

            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                }
            };

            var emailLabel = new Label
            {
                Text = friend.EmailMasked,
                FontFamily = "RobotoMedium",
                FontSize = 13,
                VerticalOptions = LayoutOptions.Center
            };

            var badge = new Label
            {
                Text = $"+{friend.BonusHours} ч (Бонус)",
                FontFamily = "RobotoBold",
                FontSize = 12,
                TextColor = Color.FromArgb("#10B981"),
                VerticalOptions = LayoutOptions.Center
            };

            grid.Add(emailLabel, 0, 0);
            grid.Add(badge, 1, 0);
            item.Content = grid;

            FriendsListContainer.Children.Add(item);
        }
    }

    private void StartMetricsTimer()
    {
        if (_metricsTimer != null)
        {
            return;
        }

        _metricsTimer = Dispatcher.CreateTimer();
        _metricsTimer.Interval = TimeSpan.FromSeconds(1);
        _metricsTimer.Tick += (_, _) => UpdateMetrics();
        _metricsTimer.Start();
    }

    private void StopMetricsTimer()
    {
        _metricsTimer?.Stop();
        _metricsTimer = null;
    }

    private void UpdateMetrics()
    {
        if (OperatingSystem.IsWindows() && OctopusEngine.ActiveRelayServer is { IsRunning: true } server)
        {
            server.Stats.SampleThroughput();
            ActiveClientsCountLabel.Text = server.Stats.ActiveClients.ToString(CultureInfo.InvariantCulture);
            CurrentThroughputLabel.Text = $"{server.Stats.CurrentMbps:F1}";
            var mb = server.Stats.BytesRelayedTotal / (1024.0 * 1024.0);
            TotalRelayedMbLabel.Text = $"{mb:F1}";

            UpdateRewardProgress(server.Stats.BytesRelayedTotal);
        }
        else
        {
            ActiveClientsCountLabel.Text = "0";
            CurrentThroughputLabel.Text = "0.0";
            TotalRelayedMbLabel.Text = "0.0";
        }

        UpdateStatusBadge();
    }

    private void UpdateRewardProgress(long totalRelayedBytes)
    {
        if (RelayRewardProgressBar == null || RelayRewardProgressLabel == null || TotalLifetimeRelayedLabel == null || ClaimRewardButton == null)
        {
            return;
        }

        var lifetimeGb = totalRelayedBytes / (1024.0 * 1024.0 * 1024.0);
        TotalLifetimeRelayedLabel.Text = $"Всего: {lifetimeGb:F1} ГБ";

        var currentCycleBytes = totalRelayedBytes % RewardThresholdBytes;
        var currentCycleGb = currentCycleBytes / (1024.0 * 1024.0 * 1024.0);
        var targetGb = RewardThresholdBytes / (1024.0 * 1024.0 * 1024.0);
        var progress = Math.Clamp((double)currentCycleBytes / RewardThresholdBytes, 0.0, 1.0);

        RelayRewardProgressBar.Progress = progress;
        RelayRewardProgressLabel.Text = $"{currentCycleGb:F1} / {targetGb:F1} ГБ ({progress * 100:F0}%)";
        ClaimRewardButton.IsEnabled = progress >= 1.0;
    }

    private void UpdateStatusBadge()
    {
        if (MeshStatusBadge == null || MeshStatusDot == null)
        {
            return;
        }

        var isRunning = OperatingSystem.IsWindows() && OctopusEngine.ActiveRelayServer is { IsRunning: true };
        if (isRunning)
        {
            var port = OctopusEngine.ActiveRelayServer?.BoundPort ?? 7443;
            var isForwarding = (OctopusEngine.ActiveRelayServer?.Stats.ActiveClients ?? 0) > 0;
            MeshStatusBadge.Text = isForwarding
                ? $"Раздача активна (:{port})"
                : $"Фоновый релей активен (:{port})";
            MeshStatusBadge.TextColor = Color.FromArgb("#10B981");
            MeshStatusDot.Color = Color.FromArgb("#10B981");
        }
        else
        {
            MeshStatusBadge.Text = "Релей неактивен";
            MeshStatusBadge.TextColor = Color.FromArgb("#EF4444");
            MeshStatusDot.Color = Color.FromArgb("#EF4444");
        }
    }

    private async void OnRelayServerToggledAsync(object? sender, ToggledEventArgs e)
    {
        if (!_isLoaded)
        {
            return;
        }

        MeshSettings.RelayEnabled = e.Value;
        if (e.Value)
        {
            await OctopusEngine.StartRelayIfEnabledAsync();
        }
        else
        {
            await OctopusEngine.StopRelayAsync();
        }
        UpdateStatusBadge();
    }

    private void OnSpeedLimitChanged(object? sender, ValueChangedEventArgs e)
    {
        if (!_isLoaded)
        {
            return;
        }

        var val = (int)e.NewValue;
        MeshSettings.RelaySpeedMbps = val;
        SpeedLimitValueLabel?.Text = $"{val} Мбит/с";
        if (OperatingSystem.IsWindows())
        {
            OctopusEngine.ActiveRelayServer?.Limiter.UpdateLimit(val);
        }
    }

    private void OnMaxClientsChanged(object? sender, EventArgs e)
    {
        if (!_isLoaded)
        {
            return;
        }

        if (MaxClientsPicker?.SelectedItem is string str && int.TryParse(str, out var count))
        {
            MeshSettings.RelayMaxClients = count;
        }
    }

    private async void OnCopyMyCodeClickedAsync(object? sender, EventArgs e)
    {
        var code = _currentMyCode;
        if (string.IsNullOrWhiteSpace(code) || code.Contains('.'))
        {
            code = MeshSettings.ReferralCode;
        }

        if (string.IsNullOrWhiteSpace(code) || code.Contains('.'))
        {
            if (_apiService != null)
            {
                await LoadReferralDataAsync();
                code = _currentMyCode;
            }
        }

        if (!string.IsNullOrWhiteSpace(code) && !code.Contains('.'))
        {
            await Clipboard.Default.SetTextAsync(code);
            if (Application.Current?.Windows.Count > 0 && Application.Current.Windows[0].Page != null)
            {
                await Application.Current.Windows[0].Page!.DisplayAlertAsync("Скопировано", $"Ваш код {code} скопирован в буфер обмена.", "OK");
            }
        }
        else
        {
            if (Application.Current?.Windows.Count > 0 && Application.Current.Windows[0].Page != null)
            {
                await Application.Current.Windows[0].Page!.DisplayAlertAsync("Внимание", "Код загружается с сервера. Попробуйте через пару секунд.", "OK");
            }
        }
    }

    private async void OnShareMyCodeClickedAsync(object? sender, EventArgs e)
    {
        var code = _currentMyCode;
        if (string.IsNullOrWhiteSpace(code) || code.Contains('.'))
        {
            code = MeshSettings.ReferralCode;
        }

        if (string.IsNullOrWhiteSpace(code) || code.Contains('.'))
        {
            if (_apiService != null)
            {
                await LoadReferralDataAsync();
                code = _currentMyCode;
            }
        }

        if (!string.IsNullOrWhiteSpace(code) && !code.Contains('.'))
        {
            await Share.Default.RequestAsync(new ShareTextRequest
            {
                Text = $"Подключайся к Obxodka VPN и получай бонусные часы! Используй мой код: {code}",
                Title = "Приглашение в Obxodka VPN"
            });
        }
        else
        {
            if (Application.Current?.Windows.Count > 0 && Application.Current.Windows[0].Page != null)
            {
                await Application.Current.Windows[0].Page!.DisplayAlertAsync("Внимание", "Код загружается с сервера. Попробуйте через пару секунд.", "OK");
            }
        }
    }

    private async void OnActivateFriendCodeClickedAsync(object? sender, EventArgs e)
    {
        var code = FriendCodeEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            if (Application.Current?.Windows.Count > 0 && Application.Current.Windows[0].Page != null)
            {
                await Application.Current.Windows[0].Page!.DisplayAlertAsync("Ошибка", "Введите код друга.", "OK");
            }
            return;
        }

        if (_apiService == null)
        {
            return;
        }

        ActivateFriendCodeButton.IsEnabled = false;
        try
        {
            var (success, resp, error) = await _apiService.ActivateReferralCodeAsync(code);
            if (success)
            {
                FriendCodeEntry.Text = string.Empty;
                if (Application.Current?.Windows.Count > 0 && Application.Current.Windows[0].Page != null)
                {
                    await Application.Current.Windows[0].Page!.DisplayAlertAsync("Успешно", resp?.Message ?? "Код активирован! Вам начислен +1 час в подарок.", "OK");
                }
                await LoadReferralDataAsync();
            }
            else
            {
                if (Application.Current?.Windows.Count > 0 && Application.Current.Windows[0].Page != null)
                {
                    await Application.Current.Windows[0].Page!.DisplayAlertAsync("Ошибка", error ?? "Не удалось активировать код.", "OK");
                }
            }
        }
        finally
        {
            ActivateFriendCodeButton.IsEnabled = true;
        }
    }

    private async void OnClaimRewardClickedAsync(object? sender, EventArgs e)
    {
        if (_apiService == null)
        {
            return;
        }

        ClaimRewardButton.IsEnabled = false;
        try
        {
            var claimId = Guid.NewGuid().ToString("N");
            var (success, resp, error) = await _apiService.ClaimReferralRewardAsync(claimId);
            if (success && resp != null)
            {
                if (Application.Current?.Windows.Count > 0 && Application.Current.Windows[0].Page != null)
                {
                    await Application.Current.Windows[0].Page!.DisplayAlertAsync("Поздравляем!", $"Вам начислено +{resp.HoursGranted} часов к подписке за раздачу в Mesh-сети!", "Отлично");
                }
                await LoadReferralDataAsync();
            }
            else
            {
                if (Application.Current?.Windows.Count > 0 && Application.Current.Windows[0].Page != null)
                {
                    await Application.Current.Windows[0].Page!.DisplayAlertAsync("Ошибка", error ?? "Не удалось забрать награду.", "OK");
                }
                ClaimRewardButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CLAIM ERROR] {ex.Message}");
            ClaimRewardButton.IsEnabled = true;
        }
    }
}
