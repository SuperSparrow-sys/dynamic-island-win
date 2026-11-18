using System.IO;
using System.Runtime.InteropServices;

namespace DynamicBay.Core;

/// <summary>
/// A window registered as a desktop app bar at the top of one display (SHAppBarMessage): Windows takes its height
/// out of the work area, so maximized windows end below it like below the taskbar. <see cref="Remove"/> must run on
/// every way out (close, exit, crash). Killed in the Task Manager no way out runs: the registered windows are kept in a
/// file, and the next start gives back the strips of those that are gone (<see cref="RemoveStale"/>).
/// </summary>
public sealed class AppBar
{
    private const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3;
    private const uint ABE_TOP = 1;
    public const int ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;

    /// <summary>Message Windows sends with ABN_* in wParam.</summary>
    public static readonly int CallbackMessage = RegisterWindowMessage("DynamicBay.AppBar");

    private static readonly HashSet<IntPtr> Registered = new();
    private readonly IntPtr _hwnd;

    public AppBar(IntPtr hwnd) => _hwnd = hwnd;

    public bool IsRegistered => Registered.Contains(_hwnd);

    public void Register()
    {
        if (IsRegistered) return;
        var d = Data();
        d.uCallbackMessage = (uint)CallbackMessage;
        if (SHAppBarMessage(ABM_NEW, ref d) != 0) { Registered.Add(_hwnd); Save(); }
        else Log.Info("Menu bar: Windows refused the app bar");
    }

    /// <summary>Claims a strip of <paramref name="heightPx"/> at the top of <paramref name="monitor"/> and returns where it ended up.</summary>
    public Native.RECT Place(Native.RECT monitor, int heightPx)
    {
        var d = Data();
        d.uEdge = ABE_TOP;
        d.rc = new Native.RECT { Left = monitor.Left, Top = monitor.Top, Right = monitor.Right, Bottom = monitor.Top + heightPx };
        SHAppBarMessage(ABM_QUERYPOS, ref d); // moves rc below bars that are already there
        d.rc.Bottom = d.rc.Top + heightPx;
        SHAppBarMessage(ABM_SETPOS, ref d);
        return d.rc;
    }

    public void Remove()
    {
        if (!Registered.Remove(_hwnd)) return;
        RemoveHandle(_hwnd);
        Save();
    }

    private static string StateFile => Path.Combine(AppSettings.Folder, "appbars.txt");

    private static void Save()
    {
        try
        {
            if (Registered.Count == 0) File.Delete(StateFile);
            else File.WriteAllLines(StateFile, Registered.Select(h => h.ToInt64().ToString()));
        }
        catch (Exception ex) { Log.Info($"Menu bar: list of bars not saved ({ex.Message})"); }
    }

    /// <summary>At start: gives back the strips a killed run left reserved (its windows no longer exist).</summary>
    public static void RemoveStale() => RemoveStale(StateFile, Native.IsWindow, RemoveHandle);

    internal static int RemoveStale(string file, Func<IntPtr, bool> isWindow, Action<IntPtr> remove)
    {
        int n = 0;
        try
        {
            if (!File.Exists(file)) return 0;
            foreach (var line in File.ReadAllLines(file))
                if (long.TryParse(line, out long v) && new IntPtr(v) is var h && h != IntPtr.Zero && !isWindow(h)) { remove(h); n++; }
            File.Delete(file);
            if (n > 0) Log.Info($"Menu bar: gave back {n} strip(s) left by a run that was killed");
        }
        catch (Exception ex) { Log.Info($"Menu bar: old bars not checked ({ex.Message})"); }
        return n;
    }

    private static void RemoveHandle(IntPtr hwnd)
    {
        var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd };
        SHAppBarMessage(ABM_REMOVE, ref d);
    }

    /// <summary>Last resort on a crash: gives every reserved strip back.</summary>
    public static void RemoveAll()
    {
        foreach (var h in Registered.ToList()) new AppBar(h).Remove();
    }

    private APPBARDATA Data() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public Native.RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
}
