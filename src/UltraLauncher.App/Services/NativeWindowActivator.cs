using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UltraLauncher.App;

/// <summary>Выводит на передний план уже открытое окно UltraLauncher при повторном запуске exe.</summary>
internal static partial class NativeWindowActivator
{
    public static void ActivateExisting(string processName)
    {
        var current = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName(processName))
        {
            using (p)
            {
                if (p.Id == current || p.MainWindowHandle == IntPtr.Zero) continue;
                ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                SetForegroundWindow(p.MainWindowHandle);
                return;
            }
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
