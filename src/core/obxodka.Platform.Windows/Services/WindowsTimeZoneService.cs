using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace obxodka.Platforms.Windows;

//нативная синхронизация системного часового пояса windows со страной vpn-сервера и отказоустойчивый откат
[SupportedOSPlatform("windows10.0.19041.0")]
public static partial class WindowsTimeZoneService
{
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint TimeZoneIdInvalid = 0xFFFFFFFF;
    private const int DynamicTimeZoneInfoSize = 432;
    private const string TimeZonesRegistryRoot = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Time Zones";
    private const string SeTimeZonePrivilegeName = "SeTimeZonePrivilege";

    private static readonly IntPtr s_hwndBroadcast = new(0xFFFF);
    private static readonly Lock s_sync = new();

    //синхронизация часового пояса windows с локацией активного vpn-сервера (если опция включена)
    public static void SyncToServerLocation(string? serverLocation, string? serverFlag = null)
    {
        if (!PlatformServices.Preferences.Get(AppPreferenceKeys.SyncTimeZoneWithServer, false))
        {
            return;
        }

        if (!ServerTimeZoneMapper.TryResolve(serverLocation, out var targetTz, serverFlag))
        {
            AppLogger.Log($"[TIMEZONE] Локация сервера '{serverLocation ?? "null"}' не сопоставлена с часовым поясом, пропускаем.");
            return;
        }

        lock (s_sync)
        {
            var currentId = GetCurrentWindowsTimeZoneId();
            var savedOriginal = PlatformServices.Preferences.Get(AppPreferenceKeys.OriginalUserTimeZoneId, string.Empty);

            //сохраняем исходный часовой пояс пользователя строго до первого переключения
            if (string.IsNullOrWhiteSpace(savedOriginal) && !string.IsNullOrWhiteSpace(currentId))
            {
                PlatformServices.Preferences.Set(AppPreferenceKeys.OriginalUserTimeZoneId, currentId);
                AppLogger.Log($"[TIMEZONE] Сохранён исходный часовой пояс пользователя: '{currentId}'.");
            }

            if (string.Equals(currentId, targetTz.WindowsId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (TryApplyWindowsTimeZoneUnsafe(targetTz.WindowsId))
            {
                AppLogger.Log($"[TIMEZONE] Часовой пояс переключён на '{targetTz.WindowsId}' ({targetTz.IanaId}, регион: {targetTz.DisplayRegion}).");
            }
        }
    }

    //гарантированное восстановление исходного часового пояса пользователя (отключение, выключение опции, краш-рекавери)
    public static void RestoreOriginalIfNeeded(string reason)
    {
        lock (s_sync)
        {
            var savedOriginal = PlatformServices.Preferences.Get(AppPreferenceKeys.OriginalUserTimeZoneId, string.Empty);
            if (string.IsNullOrWhiteSpace(savedOriginal))
            {
                return;
            }

            var currentId = GetCurrentWindowsTimeZoneId();
            if (string.Equals(currentId, savedOriginal, StringComparison.OrdinalIgnoreCase))
            {
                PlatformServices.Preferences.Remove(AppPreferenceKeys.OriginalUserTimeZoneId);
                return;
            }

            if (TryApplyWindowsTimeZoneUnsafe(savedOriginal))
            {
                PlatformServices.Preferences.Remove(AppPreferenceKeys.OriginalUserTimeZoneId);
                AppLogger.Log($"[TIMEZONE] Исходный часовой пояс '{savedOriginal}' восстановлен ({reason}).");
            }
            else
            {
                AppLogger.LogWarning($"[TIMEZONE WARN] Не удалось восстановить часовой пояс '{savedOriginal}' ({reason}); резервный ключ сохранён для повторной попытки.");
            }
        }
    }

    //получение текущего системного идентификатора часового пояса windows
    private static string GetCurrentWindowsTimeZoneId()
    {
        Span<byte> buffer = stackalloc byte[DynamicTimeZoneInfoSize];
        if (GetDynamicTimeZoneInformation(ref MemoryMarshal.GetReference(buffer)) != TimeZoneIdInvalid)
        {
            var keyName = ReadUtf16Field(buffer.Slice(172, 256));
            if (!string.IsNullOrWhiteSpace(keyName))
            {
                return keyName;
            }
        }

        TimeZoneInfo.ClearCachedData();
        return TimeZoneInfo.Local.Id;
    }

    //применение системного часового пояса через нативный win32 api без сдвига системных часов utc
    private static bool TryApplyWindowsTimeZoneUnsafe(string windowsTimeZoneId)
    {
        try
        {
            Span<byte> dtzi = stackalloc byte[DynamicTimeZoneInfoSize];
            if (!TryBuildDynamicTimeZoneBuffer(windowsTimeZoneId, dtzi))
            {
                AppLogger.LogWarning($"[TIMEZONE WARN] Не найдены данные часового пояса '{windowsTimeZoneId}' в реестре Windows.");
                return false;
            }

            if (!TryEnableTimeZonePrivilege())
            {
                AppLogger.LogWarning("[TIMEZONE WARN] Отсутствует привилегия SeTimeZonePrivilege (требуются права администратора).");
                return false;
            }

            if (SetDynamicTimeZoneInformation(in MemoryMarshal.GetReference(dtzi)) == 0)
            {
                var err = Marshal.GetLastPInvokeError();
                AppLogger.LogWarning($"[TIMEZONE WARN] SetDynamicTimeZoneInformation('{windowsTimeZoneId}') failed: win32={err}.");
                return false;
            }

            TimeZoneInfo.ClearCachedData();
            _ = SendMessageTimeoutW(s_hwndBroadcast, WmSettingChange, UIntPtr.Zero, "intl", SmtoAbortIfHung, 500, out _);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLogger.LogWarning($"[TIMEZONE WARN] Ошибка смены часового пояса '{windowsTimeZoneId}': {ex.Message}");
            return false;
        }
    }

    //заполнение 432-байтной структуры DYNAMIC_TIME_ZONE_INFORMATION из ветки реестра windows (с учетом dynamic dst)
    private static bool TryBuildDynamicTimeZoneBuffer(string timeZoneId, Span<byte> dtzi)
    {
        dtzi.Clear();
        using var tzKey = Registry.LocalMachine.OpenSubKey($@"{TimeZonesRegistryRoot}\{timeZoneId}", writable: false);
        if (tzKey is null)
        {
            return false;
        }

        byte[]? tziBytes = null;
        using (var dynDstKey = tzKey.OpenSubKey("Dynamic DST", writable: false))
        {
            if (dynDstKey is not null)
            {
                tziBytes = dynDstKey.GetValue(DateTime.UtcNow.Year.ToString(CultureInfo.InvariantCulture)) as byte[];
            }
        }

        tziBytes ??= tzKey.GetValue("TZI") as byte[];
        if (tziBytes is null || tziBytes.Length < 44)
        {
            return false;
        }

        var stdName = tzKey.GetValue("Std") as string ?? timeZoneId;
        var dltName = tzKey.GetValue("Dlt") as string ?? stdName;

        //формат reg_tzi_format (44 байта): bias(0..4), standardBias(4..8), daylightBias(8..12), standardDate(12..28), daylightDate(28..44)
        var tzi = tziBytes.AsSpan();
        tzi[..4].CopyTo(dtzi[..4]);                     //Bias
        WriteUtf16Field(dtzi.Slice(4, 64), stdName);    //StandardName[32]
        tzi.Slice(12, 16).CopyTo(dtzi.Slice(68, 16));   //StandardDate
        tzi.Slice(4, 4).CopyTo(dtzi.Slice(84, 4));      //StandardBias
        WriteUtf16Field(dtzi.Slice(88, 64), dltName);   //DaylightName[32]
        tzi.Slice(28, 16).CopyTo(dtzi.Slice(152, 16));  //DaylightDate
        tzi.Slice(8, 4).CopyTo(dtzi.Slice(168, 4));     //DaylightBias
        WriteUtf16Field(dtzi.Slice(172, 256), timeZoneId); //TimeZoneKeyName[128]
        dtzi[428] = 0;                                  //DynamicDaylightTimeDisabled = FALSE
        return true;
    }

    //включение привилегии setimezoneprivilege в токене текущего процесса
    private static bool TryEnableTimeZonePrivilege()
    {
        var tokenHandle = IntPtr.Zero;
        try
        {
            if (OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out tokenHandle) == 0 ||
                tokenHandle == IntPtr.Zero)
            {
                return false;
            }

            if (LookupPrivilegeValueW(null, SeTimeZonePrivilegeName, out var luid) == 0)
            {
                return false;
            }

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled
            };

            return AdjustTokenPrivileges(tokenHandle, 0, in privileges, 0, IntPtr.Zero, IntPtr.Zero) != 0 &&
                   Marshal.GetLastPInvokeError() == 0;
        }
        finally
        {
            if (tokenHandle != IntPtr.Zero)
            {
                _ = CloseHandle(tokenHandle);
            }
        }
    }

    private static void WriteUtf16Field(Span<byte> dstBytes, string value)
    {
        var chars = MemoryMarshal.Cast<byte, char>(dstBytes);
        chars.Clear();
        var len = Math.Min(value.Length, chars.Length - 1);
        value.AsSpan(0, len).CopyTo(chars);
    }

    private static string ReadUtf16Field(ReadOnlySpan<byte> srcBytes)
    {
        var chars = MemoryMarshal.Cast<byte, char>(srcBytes);
        var nul = chars.IndexOf('\0');
        return new string(nul >= 0 ? chars[..nul] : chars);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CloseHandle(IntPtr hObject);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetDynamicTimeZoneInformation(ref byte lpTimeZoneInformation);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int SetDynamicTimeZoneInformation(in byte lpTimeZoneInformation);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int LookupPrivilegeValueW(string? lpSystemName, string lpName, out Luid lpLuid);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int AdjustTokenPrivileges(
        IntPtr tokenHandle,
        int disableAllPrivileges,
        in TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SendMessageTimeoutW(
        IntPtr hWnd,
        uint msg,
        UIntPtr wParam,
        string lParam,
        uint fuFlags,
        uint uTimeout,
        out UIntPtr lpdwResult);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }
}
