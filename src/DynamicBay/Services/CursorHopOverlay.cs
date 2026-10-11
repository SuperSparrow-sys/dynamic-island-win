using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DynamicBay.Core;

namespace DynamicBay.Services;

/// <summary>
/// Hides CursorHop's own small pop-ups ("Receiving" next to the cursor, "Transfer complete" at the bottom right) so
/// they don't show twice next to the island's peeks. They are top-most tool windows of CursorHop-App titled
/// "cursorhop-ui" (seen with CursorHop 0.12); its settings window is a normal, larger window and stays alone.
/// Listens for windows being shown (WinEvent, out of context) on the UI thread, which pumps messages.
/// </summary>
public sealed class CursorHopOverlay : IDisposable
{
    private const uint EventObjectShow = 0x8002, OutOfContext = 0x0000;
    private const int GwlExStyle = -20, ExTopmost = 0x8, SwHide = 0;
    private readonly WinEventProc _proc; // kept alive while the hook exists
    private IntPtr _hook;
    private readonly Dictionary<uint, bool> _isCursorHop = new();
    public int Hidden { get; private set; }

    public CursorHopOverlay() => _proc = OnShow;

    public void SetEnabled(bool on)
    {
        if (on == (_hook != IntPtr.Zero)) return;
        if (on) _hook = SetWinEventHook(EventObjectShow, EventObjectShow, IntPtr.Zero, _proc, 0, 0, OutOfContext);
        else { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
    }

    public void Dispose() => SetEnabled(false);

    /// <summary>Whether a shown window is one of CursorHop's pop-ups (pure, for tests).</summary>
    internal static bool IsPopup(string processName, string title, string windowClass, int exStyle, int width, int height) =>
        processName.Equals("CursorHop-App", StringComparison.OrdinalIgnoreCase)
        && title == "cursorhop-ui"
        && windowClass == "Chrome_WidgetWin_1"
        && (exStyle & ExTopmost) != 0
        && width is > 0 and <= 480 && height is > 0 and <= 200;

    private void OnShow(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return; // only whole windows
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (!_isCursorHop.TryGetValue(pid, out bool hop))
            {
                if (_isCursorHop.Count > 256) _isCursorHop.Clear();
                try { using var p = Process.GetProcessById((int)pid); hop = p.ProcessName.Equals("CursorHop-App", StringComparison.OrdinalIgnoreCase); }
                catch { hop = false; }
                _isCursorHop[pid] = hop;
            }
            if (!hop) return;
            var title = new StringBuilder(64);
            var cls = new StringBuilder(64);
            GetWindowText(hwnd, title, title.Capacity);
            GetClassName(hwnd, cls, cls.Capacity);
            GetWindowRect(hwnd, out var r);
            if (!IsPopup("CursorHop-App", title.ToString(), cls.ToString(), GetWindowLong(hwnd, GwlExStyle), r.Right - r.Left, r.Bottom - r.Top)) return;
            ShowWindow(hwnd, SwHide);
            if (Hidden++ == 0) Log.Info("CursorHop: hiding its own pop-ups, the island shows them");
        }
        catch { }
    }

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
}
