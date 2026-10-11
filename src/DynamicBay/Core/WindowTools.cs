using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DynamicBay.Core;

public static class WindowTools
{
    /// <summary>
    /// Brings an open window back to the user: restores it if minimized (SW_RESTORE goes back to normal or maximized,
    /// as before), shows it if hidden and puts it in front. The island never takes focus, so Windows may refuse
    /// Activate; a short Topmost toggle brings the window to the front anyway.
    /// </summary>
    public static void BringBack(Window window)
    {
        // ShowWindow(SW_RESTORE) right away; SystemCommands.RestoreWindow only posts the command.
        if (window.WindowState == WindowState.Minimized) ShowWindow(new WindowInteropHelper(window).Handle, SwRestore);
        if (!window.IsVisible) window.Show();
        if (!window.Activate()) { window.Topmost = true; window.Topmost = false; }
        window.Focus();
    }

    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
