namespace obxodka.Cli;

//пути хранения конфигурации, секретов и состояния активного туннеля cli
public static class CliStoragePaths
{
    public static string GetConfigDirectory()
    {
        var custom = Environment.GetEnvironmentVariable("OBXODKA_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            _ = Directory.CreateDirectory(custom);
            return custom;
        }

        string baseDir;
        if (OperatingSystem.IsWindows())
        {
            baseDir = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Obxodka", "cli");
        }
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            baseDir = !string.IsNullOrWhiteSpace(xdg)
                ? Path.Join(xdg, "obxodka")
                : Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "obxodka");
        }

        _ = Directory.CreateDirectory(baseDir);
        return baseDir;
    }

    public static string PreferencesPath => Path.Join(GetConfigDirectory(), "preferences.json");
    public static string SecretsPath => Path.Join(GetConfigDirectory(), "secrets.json");
    public static string StatePath => Path.Join(GetConfigDirectory(), "state.json");
    public static string SplitRulesPath => Path.Join(GetConfigDirectory(), "split_rules.json");
}

//файловое хранилище настроек cli с атомарной записью
public sealed class CliPreferencesService : IPreferencesService
{
    private readonly string _path;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _data;

    public CliPreferencesService(string? path = null)
    {
        _path = path ?? CliStoragePaths.PreferencesPath;
        _data = Load();
    }

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
                var targetType = typeof(T);
                if (raw is T stringVal)
                {
                    return stringVal;
                }

                if (targetType == typeof(bool) && bool.TryParse(raw, out var b))
                {
                    return (T)(object)b;
                }

                if (targetType == typeof(int) && int.TryParse(raw, CultureInfo.InvariantCulture, out var i))
                {
                    return (T)(object)i;
                }

                if (targetType == typeof(long) && long.TryParse(raw, CultureInfo.InvariantCulture, out var l))
                {
                    return (T)(object)l;
                }

                if (targetType == typeof(double) && double.TryParse(raw, CultureInfo.InvariantCulture, out var d))
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
                var parsed = JsonSerializer.Deserialize(json, CliJsonContext.Default.DictionaryStringString);
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
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            _ = Directory.CreateDirectory(dir);
        }

        var tmp = $"{_path}.tmp";
        var json = JsonSerializer.Serialize(_data, CliJsonContext.Default.DictionaryStringString);
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, _path, overwrite: true);
    }
}

//защищенное хранилище токенов cli (windows dpapi / linux chmod 0600)
public sealed class CliSecureStorageService : ISecureStorageService
{
    private readonly string _path;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _data;

    public CliSecureStorageService(string? path = null)
    {
        _path = path ?? CliStoragePaths.SecretsPath;
        _data = Load();
    }

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
                var bytes = Encoding.UTF8.GetBytes(value);
                var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
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
            var removed = _data.Remove(key);
            if (removed)
            {
                Save();
            }

            return removed;
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
                var parsed = JsonSerializer.Deserialize(json, CliJsonContext.Default.DictionaryStringString);
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
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            _ = Directory.CreateDirectory(dir);
        }

        var tmp = $"{_path}.tmp";
        var json = JsonSerializer.Serialize(_data, CliJsonContext.Default.DictionaryStringString);
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

//сервис состояния сети для cli с реальной подпиской на смену интерфейсов ос
internal sealed class CliConnectivityService : IConnectivityService, IDisposable
{
    private EventHandler<AppConnectivityChangedEventArgs>? _connectivityChanged;
    private int _disposed;

    public CliConnectivityService()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    public AppNetworkAccess NetworkAccess =>
        NetworkInterface.GetIsNetworkAvailable() ? AppNetworkAccess.Internet : AppNetworkAccess.None;

    public event EventHandler<AppConnectivityChangedEventArgs>? ConnectivityChanged
    {
        add => _connectivityChanged += value;
        remove => _connectivityChanged -= value;
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

    private void OnNetworkChanged(object? sender, EventArgs e) => NotifyChanged();

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => NotifyChanged();

    private void NotifyChanged()
    {
        var access = NetworkAccess;
        _connectivityChanged?.Invoke(
            this,
            new AppConnectivityChangedEventArgs(access, [new AppConnectionProfile(NetworkConnectionType.Ethernet, access)]));
    }
}

//сервис информации об устройстве для cli
internal sealed class CliDeviceInfoService : IDeviceInfoService
{
    private static readonly string s_deviceId = BuildStableDeviceId();

    public string DeviceId => s_deviceId;

    private static string BuildStableDeviceId()
    {
        var cached = PlatformServices.Preferences.Get(AppPreferenceKeys.HwidCache, string.Empty);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        var seed = $"{Environment.MachineName}|{Environment.UserName}|{RuntimeInformation.OSArchitecture}|obxodka-cli";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
        PlatformServices.Preferences.Set(AppPreferenceKeys.HwidCache, hash);
        return hash;
    }
    public string Model => Environment.MachineName;
    public string Manufacturer => OperatingSystem.IsWindows() ? "Microsoft" : "Linux";
    public string Name => $"{Environment.MachineName} (CLI)";
    public string VersionString => Environment.OSVersion.VersionString;
    public string Platform => OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "Other";
    public AppDeviceIdiom Idiom => AppDeviceIdiom.Desktop;
    public AppDeviceType DeviceType => AppDeviceType.Physical;
}

//диспетчер главного потока для консольного приложения
internal sealed class CliMainThreadService : IMainThreadService
{
    public bool IsMainThread => true;
    public void BeginInvokeOnMainThread(Action action) => action();
    public Task InvokeOnMainThreadAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public Task<T> InvokeOnMainThreadAsync<T>(Func<Task<T>> func) => func();
}

//хранилище состояния активного процесса туннеля cli
public static class CliRuntimeStateStore
{
    public static void Save(CliRuntimeState state, string? path = null)
    {
        var target = path ?? CliStoragePaths.StatePath;
        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            _ = Directory.CreateDirectory(dir);
        }

        var tmp = $"{target}.tmp";
        var json = JsonSerializer.Serialize(state, CliJsonContext.Default.CliRuntimeState);
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, target, overwrite: true);
    }

    public static CliRuntimeState? Load(string? path = null)
    {
        var target = path ?? CliStoragePaths.StatePath;
        try
        {
            if (!File.Exists(target))
            {
                return null;
            }

            var json = File.ReadAllText(target);
            return JsonSerializer.Deserialize(json, CliJsonContext.Default.CliRuntimeState);
        }
        catch
        {
            return null;
        }
    }

    public static void Clear(string? path = null)
    {
        var target = path ?? CliStoragePaths.StatePath;
        try
        {
            if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
        catch
        {
            // ignored
        }
    }

    public static bool IsProcessAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
