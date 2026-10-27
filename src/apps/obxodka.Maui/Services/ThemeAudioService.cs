namespace obxodka.Maui.Services;

public static partial class ThemeAudioService
{
    private static readonly Lock t_sync = new();
    private static string? t_clickPath;
    private static string? t_hoverPath;
    private static string? t_notificationPath;
    private static string? t_connectPath;
    private static string? t_disconnectPath;
    private static string? t_defaultClickPath;
    private static double t_volume = 0.85;
    private static long t_lastHoverTicks;

#if WINDOWS
    private static Windows.Media.Playback.MediaPlayer?[]? t_sfxPool;
    private static int t_sfxPoolIndex;
#endif

    public static double Volume
    {
        get => t_volume;
        set => SetVolume(value);
    }

    public static void Configure(
        string? clickPath,
        string? hoverPath,
        string? notificationPath,
        string? connectPath,
        string? disconnectPath,
        string? ambientPath,
        double volume = 0.85)
    {
        _ = ambientPath;
        lock (t_sync)
        {
            t_clickPath = SanitizeClickPath(clickPath, connectPath);
            t_hoverPath = hoverPath;
            t_notificationPath = notificationPath;
            t_connectPath = connectPath;
            t_disconnectPath = disconnectPath;
            t_volume = Math.Clamp(volume, 0.0, 1.0);
        }
    }

    public static void Reset()
    {
        lock (t_sync)
        {
            t_clickPath = null;
            t_hoverPath = null;
            t_notificationPath = null;
            t_connectPath = null;
            t_disconnectPath = null;
        }
    }

    public static void SetVolume(double volume)
    {
        var clamped = Math.Clamp(volume, 0.0, 1.0);
        lock (t_sync)
        {
            t_volume = clamped;
#if WINDOWS
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
            {
                return;
            }

            if (t_sfxPool != null)
            {
                foreach (var player in t_sfxPool)
                {
                    if (player != null)
                    {
                        try
                        {
                            player.Volume = clamped;
                        }
                        catch
                        {
                        }
                    }
                }
            }
#endif
        }
    }

    public static void PlayClick()
    {
        var tm = ThemeManager.Instance;
        var enabled = (tm?.SoundsEnabled ?? true) && (tm?.ClickSoundsEnabled ?? true);
        if (!enabled)
        {
            return;
        }

        string? path;
        lock (t_sync)
        {
            path = t_clickPath;
        }

        path ??= GetDefaultClickPath();
        PlaySound(path, true, 0.9);
    }

    private static string? SanitizeClickPath(string? clickPath, string? connectPath)
    {
        if (string.IsNullOrWhiteSpace(clickPath) || !File.Exists(clickPath))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(connectPath) &&
            string.Equals(Path.GetFullPath(clickPath), Path.GetFullPath(connectPath), StringComparison.OrdinalIgnoreCase))
        {
            var folder = Path.GetDirectoryName(clickPath);
            if (!string.IsNullOrEmpty(folder))
            {
                var candidate = Path.Combine(folder, "click.wav");
                if (File.Exists(candidate) && !string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(connectPath), StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
            return null;
        }

        try
        {
            var info = new FileInfo(clickPath);
            if (info.Length > 90000)
            {
                var folder = Path.GetDirectoryName(clickPath);
                if (!string.IsNullOrEmpty(folder))
                {
                    var candidate = Path.Combine(folder, "click.wav");
                    if (File.Exists(candidate) && new FileInfo(candidate).Length <= 90000)
                    {
                        return candidate;
                    }
                }
                return null;
            }
        }
        catch
        {
        }

        return clickPath;
    }

    private static string? GetDefaultClickPath()
    {
        if (t_defaultClickPath != null && File.Exists(t_defaultClickPath))
        {
            return t_defaultClickPath;
        }

        try
        {
            var cache = FileSystem.CacheDirectory;
            if (string.IsNullOrEmpty(cache))
            {
                cache = Path.GetTempPath();
            }

            var path = Path.Combine(cache, "obxodka_default_click.wav");
            if (!File.Exists(path) || new FileInfo(path).Length < 100)
            {
                GenerateDefaultClickWav(path);
            }
            t_defaultClickPath = path;
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static void GenerateDefaultClickWav(string filepath)
    {
        try
        {
            const int sampleRate = 44100;
            const double durationSeconds = 0.035;
            var numSamples = (int)(sampleRate * durationSeconds);
            var dataSize = numSamples * 2 * 2;
            var totalSize = 36 + dataSize;

            using var fs = new FileStream(filepath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var bw = new BinaryWriter(fs);

            bw.Write("RIFF"u8.ToArray());
            bw.Write(totalSize);
            bw.Write("WAVE"u8.ToArray());
            bw.Write("fmt "u8.ToArray());
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)2);
            bw.Write(sampleRate);
            bw.Write(sampleRate * 2 * 2);
            bw.Write((short)4);
            bw.Write((short)16);
            bw.Write("data"u8.ToArray());
            bw.Write(dataSize);

            for (var i = 0; i < numSamples; i++)
            {
                var t = (double)i / sampleRate;
                var env = Math.Exp(-t * 110.0);
                var wave = Math.Sin(2.0 * Math.PI * 1850.0 * t) * 0.65 * env;
                wave += Math.Sin(2.0 * Math.PI * 180.0 * t) * 0.35 * Math.Exp(-t * 150.0);
                if (i < 25)
                {
                    wave += (1.0 - (i / 25.0)) * 0.4;
                }
                var clamped = Math.Clamp(wave, -1.0, 1.0);
                var sample = (short)(clamped * 22000);
                bw.Write(sample);
                bw.Write(sample);
            }
            bw.Flush();
        }
        catch
        {
        }
    }

    public static void PlayHover()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref t_lastHoverTicks) < 65)
        {
            return;
        }

        _ = Interlocked.Exchange(ref t_lastHoverTicks, now);
        var tm = ThemeManager.Instance;
        var enabled = (tm?.SoundsEnabled ?? true) && (tm?.ClickSoundsEnabled ?? true);
        PlaySound(t_hoverPath, enabled, 0.65);
    }

    public static void PlayNotification()
    {
        var enabled = ThemeManager.Instance?.SoundsEnabled ?? true;
        PlaySound(t_notificationPath, enabled, 1.0);
    }

    public static void PlayConnect()
    {
        var enabled = ThemeManager.Instance?.SoundsEnabled ?? true;
        PlaySound(t_connectPath, enabled, 1.0);
    }

    public static void PlayDisconnect()
    {
        var enabled = ThemeManager.Instance?.SoundsEnabled ?? true;
        PlaySound(t_disconnectPath, enabled, 1.0);
    }

    public static void UpdateAmbientState(bool shouldPlay) => _ = shouldPlay;

    public static void PlaySound(string? soundFilePath, bool isSoundEnabled = true, double volumeScale = 1.0)
    {
        if (!isSoundEnabled || string.IsNullOrWhiteSpace(soundFilePath) || !File.Exists(soundFilePath))
        {
            return;
        }

        var effectiveVolume = Math.Clamp(t_volume * volumeScale, 0.0, 1.0);
        if (effectiveVolume <= 0.001)
        {
            return;
        }

        try
        {
#if WINDOWS
            _ = Task.Run(() =>
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
                {
                    return;
                }

                try
                {
                    Windows.Media.Playback.MediaPlayer player;
                    lock (t_sync)
                    {
                        t_sfxPool ??= new Windows.Media.Playback.MediaPlayer?[4];
                        var idx = t_sfxPoolIndex % t_sfxPool.Length;
                        t_sfxPoolIndex = (t_sfxPoolIndex + 1) % t_sfxPool.Length;
                        player = t_sfxPool[idx] ??= new Windows.Media.Playback.MediaPlayer();
                    }

                    var fullPath = Path.GetFullPath(soundFilePath);
                    player.IsLoopingEnabled = false;
                    player.Volume = effectiveVolume;
                    player.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(fullPath));
                    player.Play();
                }
                catch
                {
                    try
                    {
                        if (Path.GetExtension(soundFilePath).Equals(".wav", StringComparison.OrdinalIgnoreCase))
                        {
                            _ = PlaySoundWin32(soundFilePath, IntPtr.Zero, 0x0001 | 0x00020000);
                        }
                    }
                    catch
                    {
                    }
                }
            });
#elif ANDROID
            _ = Task.Run(() =>
            {
                try
                {
                    var player = new Android.Media.MediaPlayer();
                    player.SetDataSource(soundFilePath);
                    player.SetVolume((float)effectiveVolume, (float)effectiveVolume);
                    player.Prepared += (s, e) => player.Start();
                    player.Completion += (s, e) =>
                    {
                        player.Release();
                        player.Dispose();
                    };
                    player.Error += (s, e) =>
                    {
                        try
                        {
                            player.Release();
                            player.Dispose();
                        }
                        catch
                        {
                        }
                    };
                    player.PrepareAsync();
                }
                catch
                {
                }
            });
#endif
        }
        catch
        {
        }
    }

#if WINDOWS
    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySoundWin32(string pszSound, IntPtr hmod, uint fdwSound);
#endif
}
