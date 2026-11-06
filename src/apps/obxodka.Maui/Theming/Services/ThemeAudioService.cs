namespace obxodka.Maui.Theming.Services;

//кроссплатформенный аудиосервис воспроизведения звуковых эффектов темы
public static partial class ThemeAudioService
{
    private static readonly Lock s_sync = new();
    private static string? s_clickPath;
    private static string? s_hoverPath;
    private static string? s_notificationPath;
    private static string? s_connectPath;
    private static string? s_disconnectPath;
    private static string? s_defaultClickPath;
    private static double s_volume = 0.85;
    private static long s_lastHoverTicks;

#if WINDOWS
    private static Windows.Media.Playback.MediaPlayer?[]? s_sfxPool;
    private static int s_sfxPoolIndex;
#endif

    public static double Volume
    {
        get => s_volume;
        set => SetVolume(value);
    }

    //настройка путей к звуковым эффектам темы
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
        lock (s_sync)
        {
            s_clickPath = SanitizeClickPath(clickPath, connectPath);
            s_hoverPath = hoverPath;
            s_notificationPath = notificationPath;
            s_connectPath = connectPath;
            s_disconnectPath = disconnectPath;
            s_volume = Math.Clamp(volume, 0.0, 1.0);
        }
    }

    //сброс привязок звуковых файлов темы
    public static void Reset()
    {
        lock (s_sync)
        {
            s_clickPath = null;
            s_hoverPath = null;
            s_notificationPath = null;
            s_connectPath = null;
            s_disconnectPath = null;
        }
    }

    //установка уровня громкости
    public static void SetVolume(double volume)
    {
        var clamped = Math.Clamp(volume, 0.0, 1.0);
        lock (s_sync)
        {
            s_volume = clamped;
#if WINDOWS
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
            {
                return;
            }

            if (s_sfxPool != null)
            {
                foreach (var player in s_sfxPool)
                {
                    if (player != null)
                    {
                        try
                        {
                            player.Volume = clamped;
                        }
                        catch
                        {
                            //подавление ошибок медиаплеера
                        }
                    }
                }
            }
#endif
        }
    }

    //воспроизведение звука клика
    public static void PlayClick()
    {
        var tm = ThemeManager.Instance;
        var enabled = (tm?.SoundsEnabled ?? true) && (tm?.ClickSoundsEnabled ?? true);
        if (!enabled)
        {
            return;
        }

        string? path;
        lock (s_sync)
        {
            path = s_clickPath;
        }

        path ??= GetDefaultClickPath();
        PlaySound(path, true, 0.9);
    }

    //проверка и санитизация пути к звуку клика
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
            //подавление ошибок проверки файла
        }

        return clickPath;
    }

    //получение пути к встроенному резервному клику
    private static string? GetDefaultClickPath()
    {
        if (s_defaultClickPath != null && File.Exists(s_defaultClickPath))
        {
            return s_defaultClickPath;
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
            s_defaultClickPath = path;
            return path;
        }
        catch
        {
            return null;
        }
    }

    //генерация синтетического щелчка в wav формате
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
            //подавление ошибок генерации аудио
        }
    }

    //воспроизведение звука при наведении курсора
    public static void PlayHover()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref s_lastHoverTicks) < 65)
        {
            return;
        }

        _ = Interlocked.Exchange(ref s_lastHoverTicks, now);
        var tm = ThemeManager.Instance;
        var enabled = (tm?.SoundsEnabled ?? true) && (tm?.ClickSoundsEnabled ?? true);
        PlaySound(s_hoverPath, enabled, 0.65);
    }

    //воспроизведение звука уведомления
    public static void PlayNotification()
    {
        var enabled = ThemeManager.Instance?.SoundsEnabled ?? true;
        PlaySound(s_notificationPath, enabled, 1.0);
    }

    //воспроизведение звука подключения vpn
    public static void PlayConnect()
    {
        var enabled = ThemeManager.Instance?.SoundsEnabled ?? true;
        PlaySound(s_connectPath, enabled, 1.0);
    }

    //воспроизведение звука отключения vpn
    public static void PlayDisconnect()
    {
        var enabled = ThemeManager.Instance?.SoundsEnabled ?? true;
        PlaySound(s_disconnectPath, enabled, 1.0);
    }

    //обновление фонового эмбиента
    public static void UpdateAmbientState(bool shouldPlay) => _ = shouldPlay;

    //воспроизведение звукового файла на текущей платформе
    public static void PlaySound(string? soundFilePath, bool isSoundEnabled = true, double volumeScale = 1.0)
    {
        if (!isSoundEnabled || string.IsNullOrWhiteSpace(soundFilePath) || !File.Exists(soundFilePath))
        {
            return;
        }

        var effectiveVolume = Math.Clamp(s_volume * volumeScale, 0.0, 1.0);
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
                    lock (s_sync)
                    {
                        s_sfxPool ??= new Windows.Media.Playback.MediaPlayer?[4];
                        var idx = s_sfxPoolIndex % s_sfxPool.Length;
                        s_sfxPoolIndex = (s_sfxPoolIndex + 1) % s_sfxPool.Length;
                        player = s_sfxPool[idx] ??= new Windows.Media.Playback.MediaPlayer();
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
                        //подавление ошибок win32 playsound
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
                            //подавление ошибок android плеера
                        }
                    };
                    player.PrepareAsync();
                }
                catch
                {
                    //подавление ошибок android плеера
                }
            });
#endif
        }
        catch
        {
            //подавление непредвиденных исключений аудио
        }
    }

#if WINDOWS
    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySoundWin32(string pszSound, IntPtr hmod, uint fdwSound);
#endif
}
