using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using DynamicBay.Core;

namespace DynamicBay.Island;

/// <summary>
/// Hides the Windows taskbar on the displays with a menu bar and lets it slide in at the bottom edge like Windows'
/// auto-hide, which Windows only offers for all displays at once. The taskbar window is hidden, the display's work area
/// is set down to its edge without a broadcast (a broadcast makes Explorer lay out and show its taskbars again), and
/// maximized windows are resized by hand (neither Windows nor Explorer does it). <see cref="RestoreAll"/> runs on every
/// way out; killed in the Task Manager, a file is left and the next start gives the taskbars back (<see cref="RestoreStale"/>).
/// </summary>
public static class TaskbarHider
{
    /// <summary>Explorer (re)started: its new taskbars are visible and the work areas are reset.</summary>
    public static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private sealed class Tray
    {
        public IntPtr Hwnd;
        public string Device = "";
        public Native.RECT Display; // refreshed by the full check, not on every tick
        public bool Shown;
        public long HeldAt;
    }

    private static readonly object Gate = new();
    private static readonly List<Tray> Hidden = new();
    private static readonly HashSet<string> Wanted = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static DispatcherTimer? _timer;
    private static long _lastFull, _fullUntil;

    /// <summary>A settings change: Explorer may lay out again, so the next ticks check everything.</summary>
    public static void CheckSoon() => _fullUntil = Clock.ElapsedMilliseconds + TaskbarMath.CheckSoonMs;

    /// <summary>Taskbars hidden on exactly these displays; all others are given back.</summary>
    public static void Apply(IEnumerable<string> devices)
    {
        lock (Gate)
        {
            Wanted.Clear();
            Wanted.UnionWith(devices);
        }
        Reapply();
    }

    public static void Reapply()
    {
        lock (Gate)
        {
            // Explorer restarted or a display went away: those windows are gone, nothing to give back.
            Hidden.RemoveAll(t => !Native.IsWindow(t.Hwnd));
            foreach (var t in Hidden.Where(t => !Wanted.Contains(t.Device)).ToList())
            {
                Hidden.Remove(t);
                Restore(t.Hwnd);
            }
            // Windows' own auto-hide already hides it everywhere.
            var monitors = Monitors.All();
            foreach (var t in Hidden)
                if (monitors.FirstOrDefault(m => m.Device == t.Device) is { } now) t.Display = now.Bounds;
            if (Wanted.Count > 0 && !AppBar.TaskbarAutoHides())
            {
                foreach (var hwnd in Trays().Where(h => Hidden.All(t => t.Hwnd != h)))
                {
                    Native.GetWindowRect(hwnd, out var r);
                    var m = monitors.FirstOrDefault(m => m.Device == TaskbarMath.DisplayOf(r, monitors.Select(x => (x.Device, x.Bounds))));
                    if (m is null || !Wanted.Contains(m.Device) || !TaskbarMath.AtBottom(r, m.Bounds)) continue;
                    Hidden.Add(new Tray { Hwnd = hwnd, Device = m.Device, Display = m.Bounds });
                    ShowWindowAsync(hwnd, SwHide);
                    if (m.Work.Bottom != m.Bounds.Bottom) SetWorkBottom(m, m.Bounds.Bottom);
                    Log.Info($"Taskbar: hidden on {m.Device}");
                }
            }
            Save();
            CheckSoon(); // taskbars hidden before (Explorer showed them again) are checked on the next ticks
            if (Hidden.Count > 0)
            {
                _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher.CurrentDispatcher);
                _timer.Start();
            }
            else _timer?.Stop();
        }
    }

    /// <summary>Every taskbar back with its strip, on every way out (also from a crash, on any thread).</summary>
    public static void RestoreAll()
    {
        lock (Gate)
        {
            Wanted.Clear();
            foreach (var t in Hidden) Restore(t.Hwnd);
            Hidden.Clear();
            Save();
        }
    }

    private static string StateFile => Path.Combine(AppSettings.Folder, "taskbars.txt");

    private static void Save()
    {
        try
        {
            if (Hidden.Count == 0) File.Delete(StateFile);
            else File.WriteAllLines(StateFile, Hidden.Select(t => t.Device));
        }
        catch (Exception ex) { Log.Info($"Taskbar: list not saved ({ex.Message})"); }
    }

    /// <summary>At start: a run that was killed left taskbars hidden; they come back (the setting hides them again).</summary>
    public static void RestoreStale()
    {
        try
        {
            if (!File.Exists(StateFile)) return;
            foreach (var hwnd in Trays().Where(h => !Native.IsWindowVisible(h))) Restore(hwnd);
            File.Delete(StateFile);
            Log.Info("Taskbar: gave back taskbars left hidden by a run that was killed");
        }
        catch (Exception ex) { Log.Info($"Taskbar: old state not checked ({ex.Message})"); }
    }

    private static void Restore(IntPtr hwnd)
    {
        if (!Native.IsWindow(hwnd)) return;
        ShowWindowAsync(hwnd, SwShowNa);
        Native.GetWindowRect(hwnd, out var r);
        var m = Monitors.All().FirstOrDefault(m => TaskbarMath.DisplayOf(r, new[] { (m.Device, m.Bounds) }) is not null);
        if (m is not null && TaskbarMath.AtBottom(r, m.Bounds) && m.Work.Bottom > r.Top) SetWorkBottom(m, r.Top);
        Log.Info($"Taskbar: back on {m?.Device}");
    }

    /// <summary>
    /// Every 50 ms only the cursor position, so the taskbar still comes at once. The rest (window and work area checks,
    /// popups) runs while the taskbar is out, when the mouse touches the edge, once a second and after a settings change.
    /// </summary>
    private static void Tick()
    {
        if (ActivityState.Current.Away) return;
        lock (Gate)
        {
            Native.GetCursorPos(out var p);
            long now = Clock.ElapsedMilliseconds;
            bool full = TaskbarMath.FullCheckDue(now, _lastFull, _fullUntil);
            if (full)
            {
                _lastFull = now;
                if (Hidden.Any(t => !Native.IsWindow(t.Hwnd))) { Reapply(); return; }
            }
            foreach (var t in Hidden)
            {
                bool atEdge = !t.Shown && TaskbarMath.AtBottomEdge(p.X, p.Y, t.Display);
                if (!TaskbarMath.NeedsCheck(t.Shown, atEdge, full)) continue;
                var m = full ? Monitors.FindDevice(t.Device) : null;
                if (full)
                {
                    if (m is null) continue;
                    t.Display = m.Bounds;
                }
                Native.GetWindowRect(t.Hwnd, out var r);
                bool visible = Native.IsWindowVisible(t.Hwnd);
                // Shown by us, or by Explorer itself for Start: held while the mouse is on it or a popup of it is open.
                bool held = (t.Shown || visible) && (TaskbarMath.Inside(p.X, p.Y, r)
                    || ShellPopupOn(GetAncestor(WindowFromPoint(p), GaRoot), t.Device) || ShellPopupOn(Native.GetForegroundWindow(), t.Device));
                if (held) t.HeldAt = now;
                bool shown = TaskbarMath.NextShown(t.Shown || held, atEdge, atEdge && MenuBarManager.FullscreenOn(t.Device), held, now - t.HeldAt);
                if (shown && !t.Shown) t.HeldAt = now;
                if (shown != visible) ShowWindowAsync(t.Hwnd, shown ? SwShowNa : SwHide);
                t.Shown = shown;
                // Explorer laid out again (a settings change, another app bar): the strip is reserved again.
                if (m is not null && m.Work.Bottom != m.Bounds.Bottom) SetWorkBottom(m, m.Bounds.Bottom);
            }
        }
    }

    private static bool ShellPopupOn(IntPtr hwnd, string device)
    {
        if (hwnd == IntPtr.Zero) return false;
        // Asked each time, never cached by process id: Windows hands ids out again.
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        return TaskbarMath.IsShellPopup(Native.ClassName(hwnd), AppWindows.ProcessPath(pid) ?? "", AppWindows.IsSwitchable(hwnd))
               && string.Equals(Monitors.Describe(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST)).Device, device, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Sets the display's work area down to <paramref name="bottom"/> (no broadcast) and maximized windows with it.</summary>
    private static void SetWorkBottom(MonitorInfo m, int bottom)
    {
        var work = m.Work;
        int from = work.Bottom;
        work.Bottom = bottom;
        if (!SystemParametersInfo(SpiSetWorkArea, 0, ref work, 0)) { Log.Info($"Taskbar: work area of {m.Device} not set"); return; }
        EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd) || !Native.IsZoomed(hwnd) || !Native.GetWindowRect(hwnd, out var r)) return true;
            if (TaskbarMath.MaximizedHeight(r, m.Bounds, from, bottom) is { } h)
                Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, r.Width, h, Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | SwpAsync);
            return true;
        }, IntPtr.Zero);
    }

    private static IEnumerable<IntPtr> Trays()
    {
        foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
            for (IntPtr h = FindWindowEx(IntPtr.Zero, IntPtr.Zero, cls, null); h != IntPtr.Zero; h = FindWindowEx(IntPtr.Zero, h, cls, null))
                yield return h;
    }

    // Async: a hung Explorer or app must not hang the UI thread (or the exit).
    private const int SwHide = 0, SwShowNa = 8, SpiSetWorkArea = 0x2F, GaRoot = 2;
    private const uint SwpAsync = 0x4000;
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(int action, int param, ref Native.RECT rect, int flags);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Native.POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
}
