using System.Security.Principal;
using obxodka.Avalonia.Services;

namespace obxodka.Avalonia.ViewModels;

//элемент списка серверов с живым пингом
public sealed partial class ServerListItem : ObservableObject
{
    public ServerListItem(VpnServerDto dto)
    {
        Dto = dto;
        Location = dto.Location;
        Provider = string.IsNullOrWhiteSpace(dto.Provider) ? "Octopus" : dto.Provider;
        Endpoint = $"{dto.Ip}:{dto.Port}";
        IsOnline = dto.IsOnline;
        PingText = dto.IsOnline ? "-- ms" : "offline";
    }

    public VpnServerDto Dto { get; }
    public string Location { get; }
    public string Provider { get; }
    public string Endpoint { get; }
    public bool IsOnline { get; }

    [ObservableProperty]
    private int? _pingMs;

    [ObservableProperty]
    private string _pingText;

    [ObservableProperty]
    private bool _isSelected;
}

//единая viewmodel десктопного клиента avalonia на реальном ядре obxodka.Client
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly HttpMessageHandler _httpHandler;
    private readonly HttpClient _httpClient;
    private readonly ApiService _apiService;
    private readonly ITunnelPlatformAdapter? _platformAdapter;
    private readonly IDisposable? _platformAdapterDisposable;
    private readonly VpnSessionOrchestrator? _orchestrator;

    private DateTimeOffset? _connectedAtUtc;
    private List<VpnServerDto> _rawServers = [];
    private bool _isLoadingSettings;
    private bool _disposed;

    //событие нового замера скорости для векторного графика без таймеров
    public event Action<long, long>? TrafficSampleReceived;

    public MainViewModel()
    {
        _httpHandler = ApiService.CreateDefaultHandler();
        _httpClient = new HttpClient(_httpHandler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestVersion = new Version(1, 1),
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        _apiService = new ApiService(_httpClient);

        if (OperatingSystem.IsLinux())
        {
            var linux = new LinuxVpnService();
            _platformAdapter = linux;
            _platformAdapterDisposable = linux;
        }
        else if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            WindowsVpnService.SyncSplitTunnelPolicy = SyncWindowsSplitPolicy;
            var win = new WindowsVpnService();
            _platformAdapter = win;
            _platformAdapterDisposable = win;
        }

        if (_platformAdapter is not null)
        {
            _orchestrator = new VpnSessionOrchestrator(_platformAdapter, hookProcessExit: false);
            _orchestrator.OnStateChanged += HandleOrchestratorStateChanged;
            _orchestrator.OnTrafficUpdated += OnVpnTrafficUpdated;
            _orchestrator.OnLogUpdated += OnVpnLogUpdated;
            _orchestrator.OnErrorOccurred += OnVpnErrorOccurred;
            _orchestrator.OnForceLogoutRequested += OnVpnForceLogout;
        }

        AppVersionText = $"v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "6.2.6"}";
        PlatformBadgeText = OperatingSystem.IsLinux() ? "Linux TUN (/dev/net/tun)" : "Windows Wintun";
        ProtocolOptions = BuildProtocolOptions();
        LoadSettingsFromPreferences();
        LoadSplitRulesFromDisk();
    }

    //навигация и состояние сайдбара
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVpnTab))]
    [NotifyPropertyChangedFor(nameof(IsProfileTab))]
    [NotifyPropertyChangedFor(nameof(IsDevicesTab))]
    [NotifyPropertyChangedFor(nameof(IsSettingsTab))]
    [NotifyPropertyChangedFor(nameof(IsSplitTab))]
    private string _activeTab = "vpn";

    [ObservableProperty]
    private bool _isLoggedIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarWidth))]
    private bool _isSidebarExpanded = true;

    public double SidebarWidth => IsSidebarExpanded ? 212 : 76;
    public bool IsVpnTab => ActiveTab == "vpn";
    public bool IsProfileTab => ActiveTab == "profile";
    public bool IsDevicesTab => ActiveTab == "devices";
    public bool IsSettingsTab => ActiveTab == "settings";
    public bool IsSplitTab => ActiveTab == "split";

    public string AppVersionText { get; }
    public string PlatformBadgeText { get; }

    //состояние авторизации
    [ObservableProperty]
    private string _authEmail = string.Empty;

    [ObservableProperty]
    private string _authCode = string.Empty;

    [ObservableProperty]
    private bool _isAuthCodeSent;

    [ObservableProperty]
    private bool _isAuthBusy;

    [ObservableProperty]
    private string _authStatusMessage = string.Empty;

    //состояние профиля (только баланс времени, без подписок)
    [ObservableProperty]
    private string _userEmail = "—";

    [ObservableProperty]
    private string _balanceHoursText = "0.0 ч";

    [ObservableProperty]
    private string _balanceDaysText = "0 дн. 0 ч";

    [ObservableProperty]
    private string _referralCode = "—";

    //состояние vpn туннеля
    [ObservableProperty]
    private AppVpnState _vpnState = AppVpnState.Disconnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeNetworkSettings))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeNetworkSettings))]
    private bool _isConnecting;

    public bool CanChangeNetworkSettings => !IsConnected && !IsConnecting;

    public IReadOnlyList<ProtocolOptionItem> ProtocolOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedProtocolDescription))]
    private ProtocolOptionItem? _selectedProtocolOption;

    public string SelectedProtocolDescription =>
        SelectedProtocolOption?.Description ?? "Умный подбор и мгновенная ротация транспортов при блокировках";

    [ObservableProperty]
    private string _vpnStatusText = "Не в сети";

    [ObservableProperty]
    private string _connectButtonText = "СТАРТ";

    [ObservableProperty]
    private IBrush _statusAccentBrush = new SolidColorBrush(Color.Parse("#60CDFF"));

    [ObservableProperty]
    private IBrush _connectRingBrush = new SolidColorBrush(Color.Parse("#0078D4"));

    [ObservableProperty]
    private IBrush _connectOuterRingBrush = new SolidColorBrush(Color.Parse("#4060CDFF"));

    [ObservableProperty]
    private IBrush _connectMiddleRingBrush = new SolidColorBrush(Color.Parse("#2660CDFF"));

    [ObservableProperty]
    private string _assignedIpText = "Не назначен";

    [ObservableProperty]
    private string _activeProtocolText = "AUTO";

    [ObservableProperty]
    private string _activeMtuText = "1420 B";

    [ObservableProperty]
    private string _nodeHostText = "Нет подключения";

    [ObservableProperty]
    private string _nodeEndpointShortText = "Нет подключения";

    [ObservableProperty]
    private string _selectedServerLocationText = "Выбор сервера...";

    [ObservableProperty]
    private string _selectedServerProviderText = "Octopus";

    [ObservableProperty]
    private string _activePingText = "-- ms";

    [ObservableProperty]
    private string _downloadSpeedText = "0 B/s";

    [ObservableProperty]
    private string _uploadSpeedText = "0 B/s";

    [ObservableProperty]
    private string _totalTrafficText = "↓ 0 B  ·  ↑ 0 B";

    [ObservableProperty]
    private string _totalTrafficShortText = "0 B";

    [ObservableProperty]
    private string _uptimeText = "00:00:00";

    [ObservableProperty]
    private bool _isServerPickerOpen;

    [ObservableProperty]
    private ServerListItem? _selectedServer;

    public ObservableCollection<ServerListItem> Servers { get; } = [];
    public ObservableCollection<DeviceItem> Devices { get; } = [];
    public ObservableCollection<string> SplitRules { get; } = [];
    public ObservableCollection<string> SessionLogs { get; } = [];

    [ObservableProperty]
    private string _newSplitRuleInput = string.Empty;

    //уведомления и ошибки
    [ObservableProperty]
    private string _bannerMessage = string.Empty;

    [ObservableProperty]
    private bool _isBannerVisible;

    [ObservableProperty]
    private bool _isBannerError;

    //настройки туннеля (ssot в PlatformServices.Preferences)
    [ObservableProperty]
    private bool _killSwitchEnabled;

    [ObservableProperty]
    private bool _autoReconnectEnabled;

    [ObservableProperty]
    private bool _adBlockDnsEnabled;

    [ObservableProperty]
    private bool _blockIpv6Enabled;

    [ObservableProperty]
    private bool _forceWebSocketEnabled;

    [ObservableProperty]
    private bool _telemetryEnabled;

    [ObservableProperty]
    private string _diagSummary = "Нажмите «Запустить диагностику» для проверки прав, DNS и API.";

    [ObservableProperty]
    private bool _isDiagBusy;

    partial void OnKillSwitchEnabledChanged(bool value)
    {
        if (!_isLoadingSettings)
        {
            PlatformServices.Preferences.Set(AppPreferenceKeys.KillSwitch, value);
        }
    }

    partial void OnAutoReconnectEnabledChanged(bool value)
    {
        if (!_isLoadingSettings)
        {
            PlatformServices.Preferences.Set(AppPreferenceKeys.AutoReconnect, value);
        }
    }

    partial void OnAdBlockDnsEnabledChanged(bool value)
    {
        if (!_isLoadingSettings)
        {
            PlatformServices.Preferences.Set(AppPreferenceKeys.UseAdblockDns, value);
        }
    }

    partial void OnBlockIpv6EnabledChanged(bool value)
    {
        if (!_isLoadingSettings)
        {
            PlatformServices.Preferences.Set(AppPreferenceKeys.BlockIpv6, value);
        }
    }

    partial void OnSelectedProtocolOptionChanged(ProtocolOptionItem? value)
    {
        if (_isLoadingSettings || value is null)
        {
            return;
        }

        var normalized = ProtocolPipelineRegistry.SetPreferredProtocol(value.Id);
        _isLoadingSettings = true;
        try
        {
            ForceWebSocketEnabled = string.Equals(normalized, "OBXODKA-WS", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    partial void OnForceWebSocketEnabledChanged(bool value)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        var normalized = ProtocolPipelineRegistry.SetPreferredProtocol(value ? "OBXODKA-WS" : ProtocolPipelineRegistry.AutoProtocolId);
        _isLoadingSettings = true;
        try
        {
            SelectedProtocolOption = FindProtocolOption(normalized);
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    partial void OnTelemetryEnabledChanged(bool value)
    {
        if (!_isLoadingSettings)
        {
            PlatformServices.Preferences.Set(AppPreferenceKeys.UseTelemetry, value);
        }
    }

    //начальная загрузка сессии, серверов и профиля
    public async Task InitializeAsync()
    {
        var session = await AuthManager.LoadSessionAsync().ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsLoggedIn = session.IsLoggedIn;
            UserEmail = string.IsNullOrWhiteSpace(session.Email) ? "—" : session.Email;
            UpdateBalanceDisplay(session.BalanceSeconds);
        });

        if (session.IsLoggedIn)
        {
            await RefreshAccountDataAsync().ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarExpanded = !IsSidebarExpanded;

    [RelayCommand]
    private void SelectTab(string tab)
    {
        ActiveTab = tab;
        if (tab == "devices" && Devices.Count == 0 && IsLoggedIn)
        {
            _ = RefreshDevicesAsync();
        }
    }

    [RelayCommand]
    private void ToggleServerPicker() => IsServerPickerOpen = !IsServerPickerOpen;

    [RelayCommand]
    private void CloseBanner() => IsBannerVisible = false;

    [RelayCommand]
    private void ChooseServer(ServerListItem item)
    {
        foreach (var s in Servers)
        {
            s.IsSelected = ReferenceEquals(s, item);
        }

        SelectedServer = item;
        SelectedServerLocationText = item.Location;
        SelectedServerProviderText = item.Provider;
        NodeHostText = $"{item.Location} ({item.Endpoint})";
        NodeEndpointShortText = item.Endpoint;
        ActivePingText = item.PingText;
        IsServerPickerOpen = false;
    }

    //запрос кода авторизации на почту
    [RelayCommand]
    private async Task RequestAuthCodeAsync()
    {
        var email = AuthEmail.Trim();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            AuthStatusMessage = "Введите корректный адрес электронной почты.";
            return;
        }

        IsAuthBusy = true;
        AuthStatusMessage = "Отправка кода подтверждения...";
        try
        {
            var (ok, err) = await _apiService.RequestCodeAsync(new EmailAuthRequest(email)).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ok)
                {
                    IsAuthCodeSent = true;
                    AuthStatusMessage = $"Код отправлен на {email}. Проверьте почту.";
                }
                else
                {
                    AuthStatusMessage = err ?? "Не удалось отправить код.";
                }
            });
        }
        finally
        {
            _ = await Dispatcher.UIThread.InvokeAsync(() => IsAuthBusy = false);
        }
    }

    //подтверждение кода и вход в аккаунт
    [RelayCommand]
    private async Task VerifyAuthCodeAsync()
    {
        var email = AuthEmail.Trim();
        var code = AuthCode.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            AuthStatusMessage = "Введите код из письма.";
            return;
        }

        IsAuthBusy = true;
        AuthStatusMessage = "Проверка кода...";
        try
        {
            var req = new EmailVerifyRequest(email, code, PlatformServices.DeviceInfo.DeviceId, PlatformServices.DeviceInfo.Name);
            var (ok, data, err) = await _apiService.VerifyCodeAsync(req).ConfigureAwait(false);
            if (!ok || data is null)
            {
                _ = await Dispatcher.UIThread.InvokeAsync(() => AuthStatusMessage = err ?? "Неверный код подтверждения.");
                return;
            }

            var session = new UserSession
            {
                Email = string.IsNullOrWhiteSpace(data.Email) ? email : data.Email,
                IsLoggedIn = true,
                JwtToken = data.Token,
                VpnConfig = data.VpnConfig,
                BalanceSeconds = data.BalanceSeconds
            };
            await AuthManager.SaveSessionAsync(session).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsLoggedIn = true;
                UserEmail = session.Email;
                UpdateBalanceDisplay(session.BalanceSeconds);
                AuthCode = string.Empty;
                IsAuthCodeSent = false;
                AuthStatusMessage = string.Empty;
                ActiveTab = "vpn";
            });

            await RefreshAccountDataAsync().ConfigureAwait(false);
        }
        finally
        {
            _ = await Dispatcher.UIThread.InvokeAsync(() => IsAuthBusy = false);
        }
    }

    [RelayCommand]
    private void ResetAuthStep()
    {
        IsAuthCodeSent = false;
        AuthCode = string.Empty;
        AuthStatusMessage = string.Empty;
    }

    //подключение или отключение vpn через реальный VpnSessionOrchestrator
    [RelayCommand]
    private async Task ToggleVpnAsync()
    {
        if (_orchestrator is null || _platformAdapter is null)
        {
            ShowBanner("Текущая ОС не поддерживает TUN-подключение.", isError: true);
            return;
        }

        if (VpnState is AppVpnState.Connected or AppVpnState.Connecting or AppVpnState.Reconnecting)
        {
            await _orchestrator.StopVpnAsync().ConfigureAwait(false);
            return;
        }

        var (hasPriv, privMsg) = CheckTunnelPrivileges();
        if (!hasPriv)
        {
            ShowBanner(privMsg, isError: true);
            return;
        }

        if (SelectedServer is null)
        {
            await RefreshServersAsync().ConfigureAwait(false);
            if (SelectedServer is null)
            {
                ShowBanner("Нет доступных серверов. Проверьте подключение к интернету.", isError: true);
                return;
            }
        }

        var preflight = await _platformAdapter.RunNetworkPreflightAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(preflight))
        {
            ShowBanner(preflight, isError: true);
            return;
        }

        var conflict = _platformAdapter.DetectConflictingVpn();
        if (!string.IsNullOrWhiteSpace(conflict))
        {
            ShowBanner($"Предупреждение: обнаружен сторонний VPN ({conflict}).", isError: false);
        }

        var target = SelectedServer.Dto;
        var fallbacks = _rawServers.Where(s => s.Ip != target.Ip).ToList();
        await _orchestrator.StartVpnAsync(target.Ip, target.Port, fallbacks).ConfigureAwait(false);
    }

    //обновление серверов с параллельным tcp-пингом
    [RelayCommand]
    private async Task RefreshServersAsync()
    {
        var (ok, servers, err) = await _apiService.GetServersAsync().ConfigureAwait(false);
        if (!ok || servers is null || servers.Count == 0)
        {
            ShowBanner($"Не удалось загрузить серверы: {err ?? "список пуст"}", isError: true);
            return;
        }

        _rawServers = servers;
        var pings = await Task.WhenAll(servers.Select(s => MeasureTcpPingMsAsync(s.Ip, s.Port))).ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var prevIp = SelectedServer?.Dto.Ip;
            Servers.Clear();
            ServerListItem? chosen = null;

            for (var i = 0; i < servers.Count; i++)
            {
                var item = new ServerListItem(servers[i])
                {
                    PingMs = pings[i],
                    PingText = pings[i] is { } ms ? $"{ms} ms" : "timeout"
                };

                if (prevIp is not null && string.Equals(servers[i].Ip, prevIp, StringComparison.OrdinalIgnoreCase))
                {
                    chosen = item;
                }

                Servers.Add(item);
            }

            chosen ??= Servers.FirstOrDefault(static s => s.IsOnline) ?? Servers.FirstOrDefault();
            if (chosen is not null)
            {
                ChooseServer(chosen);
            }
        });
    }

    //обновление списка устройств аккаунта
    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        var (ok, list, err) = await _apiService.GetDevicesAsync().ConfigureAwait(false);
        if (!ok || list is null)
        {
            ShowBanner($"Не удалось загрузить устройства: {err ?? "ошибка сети"}", isError: true);
            return;
        }

        var myHwid = PlatformServices.DeviceInfo.DeviceId;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Devices.Clear();
            foreach (var d in list)
            {
                d.IsCurrentDevice = string.Equals(d.Hwid, myHwid, StringComparison.OrdinalIgnoreCase);
                Devices.Add(d);
            }
        });
    }

    [RelayCommand]
    private async Task RemoveDeviceAsync(DeviceItem device)
    {
        if (string.IsNullOrWhiteSpace(device.Hwid))
        {
            return;
        }

        var (ok, err) = await _apiService.RemoveDeviceAsync(device.Hwid).ConfigureAwait(false);
        if (!ok)
        {
            ShowBanner($"Не удалось удалить устройство: {err ?? "ошибка"}", isError: true);
            return;
        }

        _ = await Dispatcher.UIThread.InvokeAsync(() => Devices.Remove(device));
        ShowBanner("Устройство удалено из аккаунта.", isError: false);
    }

    [RelayCommand]
    private void AddSplitRule()
    {
        var entry = NewSplitRuleInput.Trim();
        if (string.IsNullOrWhiteSpace(entry))
        {
            return;
        }

        if (!SplitRules.Contains(entry, StringComparer.OrdinalIgnoreCase))
        {
            SplitRules.Add(entry);
            SaveSplitRulesToDisk();
        }

        NewSplitRuleInput = string.Empty;
    }

    [RelayCommand]
    private void RemoveSplitRule(string rule)
    {
        if (SplitRules.Remove(rule))
        {
            SaveSplitRulesToDisk();
        }
    }

    //реальная диагностика сети, прав, dns anti-poison и api
    [RelayCommand]
    private async Task RunDiagnosticsAsync()
    {
        IsDiagBusy = true;
        DiagSummary = "Выполняется проверка прав, DNS Anti-Poison и доступности API...";
        try
        {
            var (hasPriv, privNote) = CheckTunnelPrivileges();
            var conflict = _platformAdapter?.DetectConflictingVpn();

            string[] resolved = [];
            try
            {
                var ips = await ApiService.ResolveAllHostsAntiPoisonAsync("api.obxodka.one").ConfigureAwait(false);
                resolved = [.. ips.Select(static ip => ip.ToString())];
            }
            catch
            {
                // ignored
            }

            var sw = Stopwatch.StartNew();
            var (apiOk, _, _) = await _apiService.GetServersAsync().ConfigureAwait(false);
            sw.Stop();

            var report = new StringBuilder();
            _ = report.AppendLine(CultureInfo.InvariantCulture, $"ОС: {RuntimeInformation.OSDescription}");
            _ = report.AppendLine(CultureInfo.InvariantCulture, $"Права туннеля: {(hasPriv ? "✔ OK" : "✖ Нет прав")} ({privNote})");
            _ = report.AppendLine(CultureInfo.InvariantCulture, $"API Obxodka: {(apiOk ? $"✔ Доступен ({sw.ElapsedMilliseconds} ms)" : "✖ Недоступен")}");
            _ = report.AppendLine(CultureInfo.InvariantCulture, $"DNS Anti-Poison: {(resolved.Length > 0 ? string.Join(", ", resolved) : "не удалось разрешить")}");
            _ = report.AppendLine(CultureInfo.InvariantCulture, $"Smart MTU: {SmartMtuEngine.CurrentMtu} байт");
            _ = report.Append(CultureInfo.InvariantCulture, $"Конфликты VPN: {conflict ?? "не обнаружены"}");

            _ = await Dispatcher.UIThread.InvokeAsync(() => DiagSummary = report.ToString());
        }
        finally
        {
            _ = await Dispatcher.UIThread.InvokeAsync(() => IsDiagBusy = false);
        }
    }

    //выход из аккаунта с гарантированной остановкой туннеля
    [RelayCommand]
    private async Task LogoutAsync()
    {
        if (_orchestrator is not null && _orchestrator.IsRunning)
        {
            await _orchestrator.StopVpnAsync().ConfigureAwait(false);
        }

        await AuthManager.ClearSessionAsync().ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsLoggedIn = false;
            UserEmail = "—";
            UpdateBalanceDisplay(0);
            Servers.Clear();
            Devices.Clear();
            SelectedServer = null;
            ActiveTab = "vpn";
        });
    }

    //синхронная остановка туннеля при закрытии окна
    public void ShutdownBlocking()
    {
        try
        {
            if (_orchestrator is not null && _orchestrator.IsRunning)
            {
                _ = _orchestrator.StopVpnAsync().Wait(TimeSpan.FromSeconds(4));
            }
        }
        catch
        {
            // ignored
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_orchestrator is not null)
        {
            _orchestrator.OnStateChanged -= HandleOrchestratorStateChanged;
            _orchestrator.OnTrafficUpdated -= OnVpnTrafficUpdated;
            _orchestrator.OnLogUpdated -= OnVpnLogUpdated;
            _orchestrator.OnErrorOccurred -= OnVpnErrorOccurred;
            _orchestrator.OnForceLogoutRequested -= OnVpnForceLogout;
            _orchestrator.Dispose();
        }

        _platformAdapterDisposable?.Dispose();
        _httpClient.Dispose();
        _httpHandler.Dispose();
    }

    private async Task RefreshAccountDataAsync()
    {
        var profileTask = _apiService.GetProfileAsync();
        var refTask = _apiService.GetMyReferralCodeAsync();
        var serversTask = RefreshServersAsync();

        await Task.WhenAll(profileTask, refTask, serversTask).ConfigureAwait(false);

        var (pOk, profile, _) = await profileTask.ConfigureAwait(false);
        var (rOk, refData, _) = await refTask.ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (pOk && profile is not null)
            {
                UpdateBalanceDisplay(profile.BalanceSeconds);
            }

            if (rOk && refData is not null && !string.IsNullOrWhiteSpace(refData.Code))
            {
                ReferralCode = refData.Code;
            }
        });
    }

    private void HandleOrchestratorStateChanged(AppVpnState state) =>
        Dispatcher.UIThread.Post(() =>
        {
            VpnState = state;
            IsConnected = state == AppVpnState.Connected;
            IsConnecting = state is AppVpnState.Connecting or AppVpnState.Reconnecting or AppVpnState.Disconnecting;

            (VpnStatusText, ConnectButtonText, StatusAccentBrush, ConnectRingBrush, ConnectOuterRingBrush, ConnectMiddleRingBrush) = state switch
            {
                AppVpnState.Connected => (
                    "Защищено",
                    "СТОП",
                    new SolidColorBrush(Color.Parse("#10B981")),
                    new SolidColorBrush(Color.Parse("#60CDFF")),
                    new SolidColorBrush(Color.Parse("#5010B981")),
                    new SolidColorBrush(Color.Parse("#3660CDFF"))),
                AppVpnState.Connecting or AppVpnState.Reconnecting => (
                    "Подключение...",
                    "ОТМЕНА",
                    new SolidColorBrush(Color.Parse("#F59E0B")),
                    new SolidColorBrush(Color.Parse("#F59E0B")),
                    new SolidColorBrush(Color.Parse("#48F59E0B")),
                    new SolidColorBrush(Color.Parse("#2CF59E0B"))),
                AppVpnState.Disconnecting => (
                    "Отключение...",
                    "СТОП",
                    new SolidColorBrush(Color.Parse("#F59E0B")),
                    new SolidColorBrush(Color.Parse("#F59E0B")),
                    new SolidColorBrush(Color.Parse("#48F59E0B")),
                    new SolidColorBrush(Color.Parse("#2CF59E0B"))),
                AppVpnState.Error => (
                    "Ошибка подключения",
                    "СТАРТ",
                    new SolidColorBrush(Color.Parse("#EF4444")),
                    new SolidColorBrush(Color.Parse("#EF4444")),
                    new SolidColorBrush(Color.Parse("#48EF4444")),
                    new SolidColorBrush(Color.Parse("#2CEF4444"))),
                _ => (
                    "Не в сети",
                    "СТАРТ",
                    new SolidColorBrush(Color.Parse("#60CDFF")),
                    new SolidColorBrush(Color.Parse("#0078D4")),
                    new SolidColorBrush(Color.Parse("#4060CDFF")),
                    new SolidColorBrush(Color.Parse("#2660CDFF")))
            };

            if (state == AppVpnState.Connected)
            {
                _connectedAtUtc ??= DateTimeOffset.UtcNow;
                var ip = OctopusEngine.Current.AssignedIp;
                AssignedIpText = string.IsNullOrWhiteSpace(ip) ? "100.64.0.x" : ip;
                ActiveProtocolText = OctopusEngine.Current.ActiveProtocol ?? "AUTO";
                ActiveMtuText = $"{SmartMtuEngine.CurrentMtu} B";
            }
            else if (state is AppVpnState.Disconnected or AppVpnState.Error)
            {
                _connectedAtUtc = null;
                AssignedIpText = "Не назначен";
                DownloadSpeedText = "0 B/s";
                UploadSpeedText = "0 B/s";
                UptimeText = "00:00:00";
            }
        });

    private void OnVpnTrafficUpdated(AppTrafficStats stats) =>
        Dispatcher.UIThread.Post(() =>
        {
            DownloadSpeedText = FormatSpeed(stats.DownloadSpeedBps);
            UploadSpeedText = FormatSpeed(stats.UploadSpeedBps);
            var rxTotal = OctopusEngine.Current.TotalBytesReceived;
            var txTotal = OctopusEngine.Current.TotalBytesSent;
            TotalTrafficText = $"↓ {FormatBytes(rxTotal)}  ·  ↑ {FormatBytes(txTotal)}";
            TotalTrafficShortText = FormatBytes(rxTotal + txTotal);
            ActiveMtuText = $"{SmartMtuEngine.CurrentMtu} B";
            ActiveProtocolText = OctopusEngine.Current.ActiveProtocol ?? "AUTO";
            if (_connectedAtUtc.HasValue)
            {
                var elapsed = DateTimeOffset.UtcNow - _connectedAtUtc.Value;
                UptimeText = $"{elapsed:hh\\:mm\\:ss}";
            }

            TrafficSampleReceived?.Invoke(stats.DownloadSpeedBps, stats.UploadSpeedBps);
        });

    private void OnVpnLogUpdated(string msg) =>
        Dispatcher.UIThread.Post(() =>
        {
            SessionLogs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
            while (SessionLogs.Count > 50)
            {
                SessionLogs.RemoveAt(0);
            }
        });

    private void OnVpnErrorOccurred(string error) => ShowBanner(error, isError: true);

    private void OnVpnForceLogout(string reason)
    {
        ShowBanner($"Сессия завершена: {reason}", isError: true);
        _ = LogoutAsync();
    }

    private void ShowBanner(string message, bool isError) =>
        Dispatcher.UIThread.Post(() =>
        {
            BannerMessage = message;
            IsBannerError = isError;
            IsBannerVisible = true;
        });

    private void UpdateBalanceDisplay(long balanceSeconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, balanceSeconds));
        BalanceHoursText = $"{span.TotalHours:F1} ч";
        BalanceDaysText = $"{(int)span.TotalDays} дн. {span.Hours} ч";
    }

    private void LoadSettingsFromPreferences()
    {
        _isLoadingSettings = true;
        try
        {
            KillSwitchEnabled = PlatformServices.Preferences.Get(AppPreferenceKeys.KillSwitch, true);
            AutoReconnectEnabled = PlatformServices.Preferences.Get(AppPreferenceKeys.AutoReconnect, true);
            AdBlockDnsEnabled = PlatformServices.Preferences.Get(AppPreferenceKeys.UseAdblockDns, false);
            BlockIpv6Enabled = PlatformServices.Preferences.Get(AppPreferenceKeys.BlockIpv6, false);
            var preferredProtocol = ProtocolPipelineRegistry.GetPreferredProtocol();
            SelectedProtocolOption = FindProtocolOption(preferredProtocol);
            ForceWebSocketEnabled = string.Equals(preferredProtocol, "OBXODKA-WS", StringComparison.OrdinalIgnoreCase);
            TelemetryEnabled = PlatformServices.Preferences.Get(AppPreferenceKeys.UseTelemetry, true);
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private ProtocolOptionItem FindProtocolOption(string protocolId) =>
        ProtocolOptions.FirstOrDefault(o => string.Equals(o.Id, protocolId, StringComparison.OrdinalIgnoreCase))
        ?? ProtocolOptions[0];

    private static ProtocolOptionItem[] BuildProtocolOptions()
    {
        List<ProtocolOptionItem> list =
        [
            new(
                ProtocolPipelineRegistry.AutoProtocolId,
                "Авто (Рекомендуется)",
                "Умный подбор и мгновенная ротация транспортов при блокировках")
        ];

        foreach (var factory in ProtocolPipelineRegistry.GetRegisteredFactories())
        {
            var title = factory.Kind == TransportProtocolKind.ObxodkaReality
                ? $"{factory.FriendlyName} (OBX-V8)"
                : $"{factory.FriendlyName} ({factory.TransportSummary})";
            list.Add(new ProtocolOptionItem(factory.DisplayName, title, factory.Description));
        }

        return [.. list];
    }

    private void LoadSplitRulesFromDisk()
    {
        try
        {
            if (File.Exists(AvaloniaStoragePaths.SplitRulesPath))
            {
                var json = File.ReadAllText(AvaloniaStoragePaths.SplitRulesPath);
                var list = JsonSerializer.Deserialize(json, AvaloniaJsonContext.Default.ListString);
                if (list is not null)
                {
                    foreach (var r in list)
                    {
                        SplitRules.Add(r);
                    }
                }
            }
        }
        catch
        {
            // ignored
        }
    }

    private void SaveSplitRulesToDisk()
    {
        List<string> snapshot = [.. SplitRules];
        var json = JsonSerializer.Serialize(snapshot, AvaloniaJsonContext.Default.ListString);
        File.WriteAllText(AvaloniaStoragePaths.SplitRulesPath, json, new UTF8Encoding(false));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041.0")]
    private void SyncWindowsSplitPolicy()
    {
        var rules = SplitRules.ToList();
        var policy = WindowsSplitTunnelEngine.Policy;
        policy.Enabled = rules.Count > 0;
        policy.SetBypassedExecutables(rules.Where(static r => r.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));
        policy.SetCustomRules(rules.Where(static r => !r.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));
    }

    private static (bool HasPrivileges, string Note) CheckTunnelPrivileges()
    {
        if (OperatingSystem.IsLinux())
        {
            var hasCap = LinuxTunNative.HasNetAdminPrivilege();
            var hasTun = File.Exists(LinuxTunNative.TunDevicePath);
            return hasCap && hasTun
                ? (true, "root / CAP_NET_ADMIN + /dev/net/tun")
                : (false, "Требуются права root или CAP_NET_ADMIN (sudo setcap cap_net_admin+ep ./obxodka.Avalonia).");
        }

        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var isAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            return isAdmin
                ? (true, "Администратор (Wintun)")
                : (false, "Для поднятия Wintun на Windows запустите приложение от имени администратора.");
        }

        return (false, "ОС не поддерживается.");
    }

    private static async Task<int?> MeasureTcpPingMsAsync(string ip, int port)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        using var client = new TcpClient();
        var sw = Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(ip, port, cts.Token).ConfigureAwait(false);
            sw.Stop();
            return (int)sw.ElapsedMilliseconds;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatSpeed(long bps) => bps switch
    {
        >= 1024 * 1024 => $"{bps / (1024.0 * 1024.0):F2} MB/s",
        >= 1024 => $"{bps / 1024.0:F1} KB/s",
        _ => $"{bps} B/s"
    };

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
        >= 1024L => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B"
    };
}

//пункт выпадающего списка выбора протокола подключения
public sealed record ProtocolOptionItem(string Id, string Title, string Description);
