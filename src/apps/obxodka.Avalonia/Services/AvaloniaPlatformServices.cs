namespace obxodka.Avalonia.Services;

//генератор json для хранилища настроек, секретов и правил сплит-туннеля
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class AvaloniaJsonContext : JsonSerializerContext;

//пути хранения конфигурации linux / desktop клиента
internal static class AvaloniaStoragePaths
{
    public static string ConfigDirectory
    {
        get
        {
            string dir;
            if (OperatingSystem.IsWindows())
            {
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Obxodka", "desktop");
            }
            else
            {
                var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                dir = !string.IsNullOrWhiteSpace(xdg)
                    ? Path.Combine(xdg, "obxodka")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "obxodka");
            }

            _ = Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string PreferencesPath => Path.Combine(ConfigDirectory, "preferences.json");
    public static string SecretsPath => Path.Combine(ConfigDirectory, "secrets.json");
    public static string SplitRulesPath => Path.Combine(ConfigDirectory, "split_rules.json");
}

//файловое хранилище пользовательских настроек
internal sealed class AvaloniaPreferencesService : IPreferencesService
{
    private readonly string _path = AvaloniaStoragePaths.PreferencesPath;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _data;

    public AvaloniaPreferencesService() => _data = Load();

    public T Get<T>(string key, T defaultValue = default!)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(key, out var raw))
            {
                return defaultValue;
            }

            try
            {
                var t = typeof(T);
                if (t == typeof(string))
                {
                    return (T)(object)raw;
                }

                if (t == typeof(bool) && bool.TryParse(raw, out var b))
                {
                    return (T)(object)b;
                }

                if (t == typeof(int) && int.TryParse(raw, CultureInfo.InvariantCulture, out var i))
                {
                    return (T)(object)i;
                }

                if (t == typeof(long) && long.TryParse(raw, CultureInfo.InvariantCulture, out var l))
                {
                    return (T)(object)l;
                }

                if (t == typeof(double) && double.TryParse(raw, CultureInfo.InvariantCulture, out var d))
                {
                    return (T)(object)d;
                }
            }
            catch
            {
                // ignored
            }

            return defaultValue;
        }
    }

    public void Set<T>(string key, T value)
    {
        lock (_lock)
        {
            _data[key] = value switch
            {
                null => string.Empty,
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty
            };
            Save();
        }
    }

    public bool ContainsKey(string key)
    {
        lock (_lock)
        {
            return _data.ContainsKey(key);
        }
    }

    public void Remove(string key)
    {
        lock (_lock)
        {
            if (_data.Remove(key))
            {
                Save();
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _data.Clear();
            Save();
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var parsed = JsonSerializer.Deserialize(json, AvaloniaJsonContext.Default.DictionaryStringString);
                if (parsed is not null)
                {
                    return new Dictionary<string, string>(parsed, StringComparer.Ordinal);
                }
            }
        }
        catch
        {
            // ignored
        }

        return [];
    }

    private void Save()
    {
        var tmp = $"{_path}.tmp";
        var json = JsonSerializer.Serialize(_data, AvaloniaJsonContext.Default.DictionaryStringString);
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, _path, overwrite: true);
    }
}

//защищенное хранилище токенов (linux chmod 0600 / windows dpapi)
internal sealed class AvaloniaSecureStorageService : ISecureStorageService
{
    private readonly string _path = AvaloniaStoragePaths.SecretsPath;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _data;

    public AvaloniaSecureStorageService() => _data = Load();

    public Task<string?> GetAsync(string key)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(key, out var stored) || string.IsNullOrEmpty(stored))
            {
                return Task.FromResult<string?>(null);
            }

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var encrypted = Convert.FromBase64String(stored);
                    var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                    return Task.FromResult<string?>(Encoding.UTF8.GetString(plain));
                }

                return Task.FromResult<string?>(stored);
            }
            catch
            {
                return Task.FromResult<string?>(null);
            }
        }
    }

    public Task SetAsync(string key, string value)
    {
        lock (_lock)
        {
            if (OperatingSystem.IsWindows())
            {
                var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
                _data[key] = Convert.ToBase64String(encrypted);
            }
            else
            {
                _data[key] = value;
            }

            Save();
            return Task.CompletedTask;
        }
    }

    public bool Remove(string key)
    {
        lock (_lock)
        {
            var ok = _data.Remove(key);
            if (ok)
            {
                Save();
            }

            return ok;
        }
    }

    public void RemoveAll()
    {
        lock (_lock)
        {
            _data.Clear();
            Save();
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var parsed = JsonSerializer.Deserialize(json, AvaloniaJsonContext.Default.DictionaryStringString);
                if (parsed is not null)
                {
                    return new Dictionary<string, string>(parsed, StringComparer.Ordinal);
                }
            }
        }
        catch
        {
            // ignored
        }

        return [];
    }

    private void Save()
    {
        var tmp = $"{_path}.tmp";
        var json = JsonSerializer.Serialize(_data, AvaloniaJsonContext.Default.DictionaryStringString);
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(tmp, _path, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

//мониторинг доступности и смены сетевых интерфейсов ос
internal sealed class AvaloniaConnectivityService : IConnectivityService, IDisposable
{
    private EventHandler<AppConnectivityChangedEventArgs>? _changed;
    private int _disposed;

    public AvaloniaConnectivityService()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    public AppNetworkAccess NetworkAccess =>
        NetworkInterface.GetIsNetworkAvailable() ? AppNetworkAccess.Internet : AppNetworkAccess.None;

    public event EventHandler<AppConnectivityChangedEventArgs>? ConnectivityChanged
    {
        add => _changed += value;
        remove => _changed -= value;
    }

    public Task<AppConnectionProfile> GetConnectionProfileAsync() =>
        Task.FromResult(new AppConnectionProfile(NetworkConnectionType.Ethernet, NetworkAccess));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => Notify();
    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Notify();

    private void Notify()
    {
        var access = NetworkAccess;
        _changed?.Invoke(this, new AppConnectivityChangedEventArgs(access, [new AppConnectionProfile(NetworkConnectionType.Ethernet, access)]));
    }
}

//диспетчеризация в ui-поток avalonia
internal sealed class AvaloniaMainThreadService : IMainThreadService
{
    public bool IsMainThread => Dispatcher.UIThread.CheckAccess();

    public void BeginInvokeOnMainThread(Action action)
    {
        if (IsMainThread)
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    public async Task InvokeOnMainThreadAsync(Action action)
    {
        if (IsMainThread)
        {
            action();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(action);
    }

    public async Task<T> InvokeOnMainThreadAsync<T>(Func<Task<T>> func) =>
        IsMainThread
            ? await func().ConfigureAwait(false)
            : await Dispatcher.UIThread.InvokeAsync(func);
}

//идентификация десктоп-устройства linux/windows
internal sealed class AvaloniaDeviceInfoService : IDeviceInfoService
{
    private static readonly string s_deviceId = BuildDeviceId();

    public string DeviceId => s_deviceId;
    public string Model => Environment.MachineName;
    public string Manufacturer => OperatingSystem.IsLinux() ? "Linux" : "Microsoft";
    public string Name => OperatingSystem.IsLinux() ? $"{Environment.MachineName} (Linux)" : $"{Environment.MachineName} (Desktop)";
    public string VersionString => Environment.OSVersion.VersionString;
    public string Platform => OperatingSystem.IsLinux() ? "Linux" : "Windows";
    public AppDeviceIdiom Idiom => AppDeviceIdiom.Desktop;
    public AppDeviceType DeviceType => AppDeviceType.Physical;

    private static string BuildDeviceId()
    {
        var cached = PlatformServices.Preferences.Get(AppPreferenceKeys.HwidCache, string.Empty);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        var seed = $"{Environment.MachineName}|{Environment.UserName}|{RuntimeInformation.OSArchitecture}|obxodka-desktop";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
        PlatformServices.Preferences.Set(AppPreferenceKeys.HwidCache, hash);
        return hash;
    }
}
