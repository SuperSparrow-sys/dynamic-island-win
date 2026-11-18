namespace DynamicBay.Core;

/// <summary>Pure rules for hiding the Windows taskbar on displays with a menu bar, testable without a window.</summary>
public static class TaskbarMath
{
    /// <summary>How long the taskbar stays after the mouse left it, like Windows' own auto-hide.</summary>
    public const int HideDelayMs = 400;

    /// <summary>Maximized windows end this far beyond the work area (their invisible frame, larger at high DPI).</summary>
    public const int FrameSlackPx = 32;

    /// <summary>The display a taskbar belongs to: the one holding its centre (a hidden taskbar keeps its place).</summary>
    public static string? DisplayOf(Native.RECT tray, IEnumerable<(string Device, Native.RECT Bounds)> displays)
    {
        int x = tray.Left + tray.Width / 2, y = tray.Top + tray.Height / 2;
        foreach (var (device, b) in displays)
            if (x >= b.Left && x < b.Right && y >= b.Top && y < b.Bottom) return device;
        return null;
    }

    /// <summary>A taskbar docked at the bottom of its display, the only place Windows 11 puts it.</summary>
    public static bool AtBottom(Native.RECT tray, Native.RECT display) =>
        tray.Bottom == display.Bottom && tray.Left <= display.Left && tray.Right >= display.Right && tray.Top > display.Top + display.Height / 2;

    /// <summary>The mouse touches the bottom edge of this display.</summary>
    public static bool AtBottomEdge(int x, int y, Native.RECT display) =>
        x >= display.Left && x < display.Right && y >= display.Bottom - 2 && y < display.Bottom;

    public static bool Inside(int x, int y, Native.RECT r) => x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;

    /// <summary>
    /// Whether a hidden taskbar is shown next. It comes at the bottom edge (not over a film or game) and goes once
    /// nothing has held it (mouse over it, Start, a jump list or flyout of it) for <see cref="HideDelayMs"/>.
    /// </summary>
    public static bool NextShown(bool shown, bool atEdge, bool fullscreen, bool held, long msSinceHeld) =>
        shown ? held || msSinceHeld < HideDelayMs : atEdge && !fullscreen;

    /// <summary>
    /// Start, search, jump lists, thumbnails, the tray overflow and quick settings: windows of the shell that are no
    /// app windows (File Explorer runs in explorer.exe too, but is one; Start has a title and looks like one, but is
    /// not). Menus count for any app (a tray icon's menu). The taskbar itself and the desktop do not hold it.
    /// </summary>
    public static bool IsShellPopup(string className, string process, bool appWindow) =>
        className == "#32768"
        || (className is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW")
            && process.ToLowerInvariant() switch
            {
                "explorer" => !appWindow,
                "startmenuexperiencehost" or "searchhost" or "searchapp" or "shellexperiencehost" or "shellhost" => true,
                _ => false,
            });

    /// <summary>
    /// New height of a maximized window when the work area's bottom moves from <paramref name="from"/> to
    /// <paramref name="to"/>, or null if it is not one that ended there (another display, a fullscreen window).
    /// </summary>
    public static int? MaximizedHeight(Native.RECT window, Native.RECT display, int from, int to)
    {
        int x = window.Left + window.Width / 2, y = window.Top + window.Height / 2;
        if (!Inside(x, y, display) || window.Bottom < from || window.Bottom > from + FrameSlackPx) return null;
        return window.Height + to - from;
    }
}
