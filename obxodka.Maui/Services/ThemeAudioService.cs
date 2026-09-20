namespace obxodka.Maui.Services;

public static partial class ThemeAudioService
{
    public static void PlaySound(string? soundFilePath, bool isSoundEnabled = true)
    {
        if (!isSoundEnabled || string.IsNullOrWhiteSpace(soundFilePath) || !File.Exists(soundFilePath))
        {
            return;
        }

        try
        {
#if WINDOWS
            if (Path.GetExtension(soundFilePath).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        _ = PlaySoundWin32(soundFilePath, IntPtr.Zero, 0x0001 | 0x00020000);
                    }
                    catch
                    {
                    }
                });
            }
#elif ANDROID
            _ = Task.Run(() =>
            {
                try
                {
                    var player = new Android.Media.MediaPlayer();
                    player.SetDataSource(soundFilePath);
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

