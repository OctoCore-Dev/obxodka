namespace obxodka.Avalonia.ViewModels;

public record ServerLocation(string Country, string City, string CountryCode, string Provider, int Ping, string Host);

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSidebarVisible))]
    private string _currentTab = "vpn";

    public bool IsSidebarVisible => CurrentTab != "auth";

    [ObservableProperty]
    private bool _isSidebarExpanded;

    [ObservableProperty]
    private double _sidebarWidth = 76;

    [ObservableProperty]
    private bool _isConnected = true;

    [ObservableProperty]
    private string _connectButtonText = "СТОП";

    [ObservableProperty]
    private string _vpnStatusText = "Защищено";

    [ObservableProperty]
    private IBrush _vpnStatusDotBrush = new SolidColorBrush(Color.Parse("#00E5FF"));

    [ObservableProperty]
    private string _ipAddress = "";

    [ObservableProperty]
    private string _nodeHost = "";

    [ObservableProperty]
    private string _pingText = "";

    [ObservableProperty]
    private string _trafficUpText = "";

    [ObservableProperty]
    private string _trafficDownText = "";

    [ObservableProperty]
    private string _totalTrafficText = "";

    [ObservableProperty]
    private string _remainingTimeText = "";

    [ObservableProperty]
    private string _userEmail = "";

    [ObservableProperty]
    private string _appVersion = "";

    [ObservableProperty]
    private bool _isServerModalOpen;

    [ObservableProperty]
    private ServerLocation _selectedServer;

    [ObservableProperty]
    private ObservableCollection<ServerLocation> _servers = [];

    // Dialog state
    [ObservableProperty]
    private bool _isDialogOpen;

    [ObservableProperty]
    private string _dialogTitle = "";

    [ObservableProperty]
    private string _dialogMessage = "";

    [ObservableProperty]
    private string _dialogConfirmText = "OK";

    [ObservableProperty]
    private string _dialogCancelText = "Отмена";

    [ObservableProperty]
    private bool _isDialogCancelVisible;

    [ObservableProperty]
    private StreamGeometry? _dialogIconGeometry;

    [ObservableProperty]
    private IBrush _dialogIconBrush = new SolidColorBrush(Color.Parse("#FF5252"));

    [ObservableProperty]
    private IBrush _dialogIconBgBrush = new SolidColorBrush(Color.FromArgb(35, 255, 82, 82));

    private Action? _dialogConfirmCallback;

    // Configuration states
    [ObservableProperty]
    private bool _isAdBlockEnabled = true;

    [ObservableProperty]
    private bool _isAutoConnectEnabled;

    [ObservableProperty]
    private bool _isKillSwitchEnabled = true;

    public MainViewModel()
    {
        Servers =
        [
            new ServerLocation("Швеция", "Стокгольм", "SE", "Vultr", 21, "se-sto-01."),
            new ServerLocation("Германия", "Франкфурт", "DE", "Hetzner", 24, "de-fra-01."),
            new ServerLocation("Нидерланды", "Амстердам", "NL", "DigitalOcean", 28, "nl-ams-01."),
            new ServerLocation("Финляндия", "Хельсинки", "FI", "Hetzner", 22, "fi-hel-01."),
            new ServerLocation("Турция", "Стамбул", "TR", "Equinix", 45, "tr-ist-01")
        ];

        SelectedServer = Servers[0];
    }

    [RelayCommand]
    public void ToggleSidebar()
    {
        IsSidebarExpanded = !IsSidebarExpanded;
        SidebarWidth = IsSidebarExpanded ? 260 : 76;
    }

    [RelayCommand]
    public void SelectTab(string tab) => CurrentTab = tab;

    [RelayCommand]
    public void ToggleConnect()
    {
        IsConnected = !IsConnected;
        if (IsConnected)
        {
            ConnectButtonText = "СТОП";
            VpnStatusText = "Защищено";
            VpnStatusDotBrush = new SolidColorBrush(Color.Parse("#00E5FF"));
            IpAddress = "";
            NodeHost = SelectedServer.Host;
            PingText = $"{SelectedServer.Ping} ms";
            TrafficUpText = "734,1 KB/s";
            TrafficDownText = "3,2 MB/s";
            TotalTrafficText = "213,8 MB";
        }
        else
        {
            ConnectButtonText = "СТАРТ";
            VpnStatusText = "Отключен";
            VpnStatusDotBrush = new SolidColorBrush(Color.Parse("#90FFFFFF"));
            IpAddress = "Не назначен";
            NodeHost = "Нет подключения";
            PingText = "-- ms";
            TrafficUpText = "0 B/s";
            TrafficDownText = "0 B/s";
        }
    }

    [RelayCommand]
    public void OpenServerModal() => IsServerModalOpen = true;

    [RelayCommand]
    public void CloseServerModal() => IsServerModalOpen = false;

    [RelayCommand]
    public void SelectServer(ServerLocation server)
    {
        SelectedServer = server;
        if (IsConnected)
        {
            NodeHost = server.Host;
            PingText = $"{server.Ping} ms";
        }

        IsServerModalOpen = false;
    }

    [RelayCommand]
    public void RefreshServers()
    {
        Servers = [.. Servers];
    }

    [RelayCommand]
    public void Logout()
    {
        ShowDialog("Выход", "Вы действительно хотите выйти из аккаунта?", "Да", "Отмена", () => CurrentTab = "auth");
    }

    public void ShowDialog(string title, string message, string confirmText, string? cancelText, Action? onConfirm)
    {
        DialogTitle = title;
        DialogMessage = message;
        DialogConfirmText = confirmText;
        DialogCancelText = cancelText ?? "";
        IsDialogCancelVisible = !string.IsNullOrEmpty(cancelText);
        _dialogConfirmCallback = onConfirm;

        ConfigureDialogStyling(title);

        IsDialogOpen = true;
    }

    private void ConfigureDialogStyling(string title)
    {
        var lower = title.ToLowerInvariant();
        if (lower.Contains("выход") || lower.Contains("удал"))
        {
            DialogIconGeometry = FindIcon("IconExit20");
            DialogIconBrush = new SolidColorBrush(Color.Parse("#FF5252"));
            DialogIconBgBrush = new SolidColorBrush(Color.FromArgb(35, 255, 82, 82));
        }
        else if (lower.Contains("ошибк") || lower.Contains("сбой"))
        {
            DialogIconGeometry = FindIcon("IconDismiss16");
            DialogIconBrush = new SolidColorBrush(Color.Parse("#FF5252"));
            DialogIconBgBrush = new SolidColorBrush(Color.FromArgb(35, 255, 82, 82));
        }
        else if (lower.Contains("внимани") || lower.Contains("лимит"))
        {
            DialogIconGeometry = FindIcon("IconWarning24");
            DialogIconBrush = new SolidColorBrush(Color.Parse("#FFB74D"));
            DialogIconBgBrush = new SolidColorBrush(Color.FromArgb(35, 255, 183, 77));
        }
        else
        {
            DialogIconGeometry = FindIcon("IconInfo24");
            DialogIconBrush = new SolidColorBrush(Color.Parse("#00E5FF"));
            DialogIconBgBrush = new SolidColorBrush(Color.FromArgb(35, 0, 229, 255));
        }
    }

    private static StreamGeometry? FindIcon(string key)
    {
        if (Application.Current?.TryFindResource(key, out var res) == true && res is StreamGeometry g)
        {
            return g;
        }

        return null;
    }

    [RelayCommand]
    public void OpenBugTracker()
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo("https://obxodka.one/BugTracker/Index") { UseShellExecute = true });
        }
        catch
        {
            ShowDialog("Баг-трекер", "Перейдите по ссылке для отправки отчёта:\nhttps://obxodka.one/BugTracker/Index", "OK", null, null);
        }
    }

    [RelayCommand]
    public void OpenReviews()
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo("https://obxodka.one/Reviews/Index") { UseShellExecute = true });
        }
        catch
        {
            ShowDialog("Отзывы", "Перейдите по ссылке для просмотра отзывов:\nhttps://obxodka.one/Reviews/Index", "OK", null, null);
        }
    }

    [RelayCommand]
    public async Task CopyIpAddressAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow.Clipboard: { } clipboard })
        {
            try
            {
                using var dt = new DataTransfer();
                dt.Add(DataTransferItem.CreateText(IpAddress));
                await clipboard.SetDataAsync(dt);
            }
            catch
            {
            }
        }

        ShowDialog("IP скопирован", $"IP-адрес {IpAddress} успешно скопирован в буфер обмена.", "OK", null, null);
    }

    [RelayCommand]
    public void ExtendSubscription()
    {
        ShowDialog("Подписка", "Ваша подписка активна (30 дн. 14 ч.).\nДля продления или смены тарифа перейдите в личный кабинет.", "OK", null, null);
    }

    [RelayCommand]
    public void ConfigureSplitTunneling()
    {
        ShowDialog("Раздельное туннелирование", "Раздельное туннелирование работает в автоматическом режиме. Локальные сервисы и банковские приложения направляются в обход VPN-канала.", "OK", null, null);
    }

    [RelayCommand]
    public void DisconnectDevice()
    {
        ShowDialog("Отключение устройства", "Вы действительно хотите отключить устройство Samsung Galaxy S24?", "Отключить", "Отмена", () => ShowDialog("Устройство отключено", "Устройство Samsung Galaxy S24 успешно отключено от вашего аккаунта.", "OK", null, null));
    }

    [RelayCommand]
    public void ConfirmDialog()
    {
        IsDialogOpen = false;
        _dialogConfirmCallback?.Invoke();
        _dialogConfirmCallback = null;
    }

    [RelayCommand]
    public void CancelDialog()
    {
        IsDialogOpen = false;
        _dialogConfirmCallback = null;
    }
}
