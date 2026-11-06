namespace obxodka.Platforms.Windows;

//диалог выбора исполняемого файла через win32 comdlg32
public sealed partial class AppManager
{
#pragma warning disable IDE1006, IDE0017, IDE0046, SYSLIB1054
    //структура win32 для функции открытия файла getopenfilenamew
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAMEW
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string? lpstrFilter;
        public string? lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAMEW ofn);

    //отображение системного диалога выбора exe с фильтрацией и привязкой к главному окну
    public static string? PickExecutableFile()
    {
        var ofn = new OPENFILENAMEW();
        ofn.lStructSize = Marshal.SizeOf<OPENFILENAMEW>();
        ofn.lpstrFilter = "Исполняемые файлы (*.exe)\0*.exe\0Все файлы (*.*)\0*.*\0\0";
        ofn.nFilterIndex = 1;

        var buffer = Marshal.AllocHGlobal(2048 * sizeof(char));
        try
        {
            Marshal.Copy(new char[2048], 0, buffer, 2048);
            ofn.lpstrFile = buffer;
            ofn.nMaxFile = 2048;
            ofn.lpstrTitle = "Выберите исполняемый файл (.exe) для обхода VPN";

            //флаги: ofn_explorer | ofn_filemustexist | ofn_pathmustexist | ofn_nochangdir
            ofn.Flags = 0x00081808;

            if (Application.Current?.Windows.Count > 0 &&
                Application.Current.Windows[0]?.Handler?.PlatformView is Microsoft.UI.Xaml.Window win)
            {
                ofn.hwndOwner = WinRT.Interop.WindowNative.GetWindowHandle(win);
            }

            if (GetOpenFileNameW(ref ofn))
            {
                return Marshal.PtrToStringUni(buffer);
            }

            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
#pragma warning restore IDE1006, IDE0017, IDE0046, SYSLIB1054
}
