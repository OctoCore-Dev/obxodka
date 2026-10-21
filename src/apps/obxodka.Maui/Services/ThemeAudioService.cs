namespace obxodka.Maui.Services;

public static partial class ThemeAudioService
{
    private static readonly Lock t_sync = new();
    private static string? t_clickPath;
    private static string? t_hoverPath;
    private static string? t_notificationPath;
    private static string? t_connectPath;
    private static string? t_disconnectPath;
    private static string? t_ambientPath;
    private static double t_volume = 0.85;
    private static bool t_ambientShouldPlay;
    private static long t_lastHoverTicks;

#if WINDOWS
    private static Windows.Media.Playback.MediaPlayer?[]? t_sfxPool;
    private static int t_sfxPoolIndex;
    private static Windows.Media.Playback.MediaPlayer? t_ambientPlayer;
    private static string? t_loadedAmbientPath;
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
        lock (t_sync)
        {
            t_clickPath = clickPath;
            t_hoverPath = hoverPath;
            t_notificationPath = notificationPath;
            t_connectPath = connectPath;
            t_disconnectPath = disconnectPath;
            t_ambientPath = ambientPath;
            t_volume = Math.Clamp(volume, 0.0, 1.0);
        }

        RefreshAmbientPlayback();
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
            t_ambientPath = null;
            t_ambientShouldPlay = false;
        }

        StopAmbientInternal();
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

            if (t_ambientPlayer != null)
            {
                try
                {
                    t_ambientPlayer.Volume = Math.Clamp(clamped * 0.45, 0.0, 1.0);
                }
                catch
                {
                }
            }
#endif
        }
    }

    public static void PlayClick()
    {
        var tm = ThemeManager.Instance;
        var enabled = (tm?.SoundsEnabled ?? true) && (tm?.ClickSoundsEnabled ?? true);
        PlaySound(t_clickPath, enabled, 0.9);
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

    public static void UpdateAmbientState(bool shouldPlay)
    {
        lock (t_sync)
        {
            t_ambientShouldPlay = shouldPlay;
        }

        RefreshAmbientPlayback();
    }

    private static void RefreshAmbientPlayback()
    {
        var soundsEnabled = ThemeManager.Instance?.SoundsEnabled ?? true;
        var ambientEnabled = ThemeManager.Instance?.AmbientEnabled ?? true;
        string? path;
        bool shouldPlay;
        double vol;

        lock (t_sync)
        {
            path = t_ambientPath;
            shouldPlay = t_ambientShouldPlay && soundsEnabled && ambientEnabled && !string.IsNullOrWhiteSpace(path) && File.Exists(path);
            vol = Math.Clamp(t_volume * 0.45, 0.0, 1.0);
        }

        if (!shouldPlay || string.IsNullOrWhiteSpace(path))
        {
            StopAmbientInternal();
            return;
        }

#if WINDOWS
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
        {
            return;
        }

        try
        {
            lock (t_sync)
            {
                t_ambientPlayer ??= new Windows.Media.Playback.MediaPlayer
                {
                    IsLoopingEnabled = true
                };

                var fullPath = Path.GetFullPath(path);
                if (!string.Equals(t_loadedAmbientPath, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    t_ambientPlayer.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(fullPath));
                    t_loadedAmbientPath = fullPath;
                }

                t_ambientPlayer.IsLoopingEnabled = true;
                t_ambientPlayer.Volume = vol;
                t_ambientPlayer.Play();
            }
        }
        catch
        {
        }
#endif
    }

    private static void StopAmbientInternal()
    {
#if WINDOWS
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
        {
            return;
        }

        try
        {
            lock (t_sync)
            {
                if (t_ambientPlayer != null)
                {
                    t_ambientPlayer.Pause();
                    t_ambientPlayer.Source = null;
                    t_loadedAmbientPath = null;
                }
            }
        }
        catch
        {
        }
#endif
    }

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
