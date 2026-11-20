using WinRT.Interop;
using Window = Microsoft.UI.Xaml.Window;

namespace obxodka.Platforms.Windows;

//диалог выбора исполняемого файла через win32 comdlg32
public sealed partial class AppManager
{
    [LibraryImport("comdlg32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetOpenFileNameW(ref OpenFileNameW ofn);

    //отображение системного диалога выбора exe с фильтрацией и привязкой к главному окну
    public static string? PickExecutableFile()
    {
        var buffer = Marshal.AllocHGlobal(2048 * sizeof(char));
        var filterPtr = Marshal.StringToHGlobalUni("Исполняемые файлы (*.exe)\0*.exe\0Все файлы (*.*)\0*.*\0\0");
        var titlePtr = Marshal.StringToHGlobalUni("Выберите исполняемый файл (.exe) для обхода VPN");
        try
        {
            Marshal.WriteInt16(buffer, 0);

            var ofn = new OpenFileNameW
            {
                LStructSize = Marshal.SizeOf<OpenFileNameW>(),
                LpstrFilter = filterPtr,
                NFilterIndex = 1,
                LpstrFile = buffer,
                NMaxFile = 2048,
                LpstrTitle = titlePtr,
                //флаги: ofn_explorer | ofn_filemustexist | ofn_pathmustexist | ofn_nochangdir
                Flags = 0x00081808
            };

            if (Application.Current?.Windows.Count > 0 &&
                Application.Current.Windows[0].Handler?.PlatformView is Window win)
            {
                ofn.HwndOwner = WindowNative.GetWindowHandle(win);
            }

            return GetOpenFileNameW(ref ofn) ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(titlePtr);
            Marshal.FreeHGlobal(filterPtr);
            Marshal.FreeHGlobal(buffer);
        }
    }

    //структура win32 для функции открытия файла get open filename (blittable для library import)
    [StructLayout(LayoutKind.Sequential)]
    private struct OpenFileNameW
    {
        public int LStructSize;
        public IntPtr HwndOwner;
        public IntPtr HInstance;
        public IntPtr LpstrFilter;
        public IntPtr LpstrCustomFilter;
        public int NMaxCustFilter;
        public int NFilterIndex;
        public IntPtr LpstrFile;
        public int NMaxFile;
        public IntPtr LpstrFileTitle;
        public int NMaxFileTitle;
        public IntPtr LpstrInitialDir;
        public IntPtr LpstrTitle;
        public int Flags;
        public short NFileOffset;
        public short NFileExtension;
        public IntPtr LpstrDefExt;
        public IntPtr LCustData;
        public IntPtr LpfnHook;
        public IntPtr LpTemplateName;
        public IntPtr PvReserved;
        public int DwReserved;
        public int FlagsEx;
    }
}
