using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DynamicBay.Core;

/// <summary>
/// Pinned apps behave like the taskbar: a click brings an open window of the app to the front (or minimizes it when it
/// is already in front) and starts the app only when none is open.
/// </summary>
public static class AppWindows
{
    /// <summary>
    /// Whether a window belongs to the pinned entry. <paramref name="launchPath"/> is "shell:AppsFolder\{AppUserModelId}"
    /// (Store apps, apps with their own id, or a program path that starts with a known-folder GUID) or a file (.exe, .lnk
    /// already resolved to <paramref name="shortcutTarget"/>). <paramref name="knownFolder"/> turns "{GUID}" into a path.
    /// </summary>
    public static bool Matches(string launchPath, string? windowAppId, string? exePath, string? shortcutTarget, Func<Guid, string?> knownFolder)
    {
        const string apps = @"shell:AppsFolder\";
        if (launchPath.StartsWith(apps, StringComparison.OrdinalIgnoreCase))
        {
            string id = launchPath[apps.Length..];
            if (windowAppId is not null && string.Equals(id, windowAppId, StringComparison.OrdinalIgnoreCase)) return true;
            // A classic program without its own id: the Start menu lists it by its path, the folder as a GUID.
            if (exePath is null || !id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
            string path = id;
            if (id.StartsWith('{') && id.IndexOf('}') is > 0 and var end && Guid.TryParse(id[..(end + 1)], out var folder))
            {
                if (knownFolder(folder) is not { } root) return false;
                path = root + id[(end + 1)..];
            }
            return SamePath(path, exePath);
        }
        if (exePath is null) return false;
        return SamePath(shortcutTarget ?? launchPath, exePath);
    }

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>The topmost open window of the app (as in Alt+Tab), or zero.</summary>
    public static IntPtr Find(string launchPath)
    {
        string? target = launchPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(launchPath) : null;
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsSwitchable(hwnd)) return true;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == Environment.ProcessId) return true;
            if (Matches(launchPath, TaskbarIdentity.Read(hwnd), ProcessPath(pid), target, KnownFolder)) { found = hwnd; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Like a taskbar button: in front and not minimized → minimize; otherwise restore and bring to the front.</summary>
    public static void Toggle(IntPtr hwnd)
    {
        if (Native.GetForegroundWindow() == hwnd && !IsIconic(hwnd)) { ShowWindowAsync(hwnd, SwMinimize); return; }
        if (IsIconic(hwnd)) ShowWindowAsync(hwnd, SwRestore);
        if (SetForegroundWindow(hwnd)) return;
        // Windows may refuse the foreground to a process without focus: a tapped Alt key lifts the lock (as the taskbar does).
        keybd_event(VkMenu, 0, 0, UIntPtr.Zero);
        keybd_event(VkMenu, 0, KeyUp, UIntPtr.Zero);
        if (!SetForegroundWindow(hwnd)) Log.Info("Menu bar: Windows did not bring the app to the front");
    }

    /// <summary>A window Alt+Tab would show: visible, not cloaked, no owner, not a tool window, with a title.</summary>
    private static bool IsSwitchable(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != IntPtr.Zero) return false;
        if ((Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64() & Native.WS_EX_TOOLWINDOW) != 0) return false;
        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        return GetWindowTextLength(hwnd) > 0;
    }

    private static string? ProcessPath(uint pid)
    {
        IntPtr h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    private static string? KnownFolder(Guid id)
    {
        try
        {
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) != 0) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        catch { return null; }
    }

    private static string? ShortcutTarget(string lnk)
    {
        try
        {
            var shell = Type.GetTypeFromProgID("WScript.Shell");
            if (shell is null) return null;
            dynamic ws = Activator.CreateInstance(shell)!;
            try
            {
                string target = ws.CreateShortcut(lnk).TargetPath;
                return target.Length > 0 ? target : null;
            }
            finally { Marshal.FinalReleaseComObject(ws); }
        }
        catch { return null; }
    }

    private const int SwMinimize = 6, SwRestore = 9, GwOwner = 4, DwmwaCloaked = 14;
    private const byte VkMenu = 0x12;
    private const uint KeyUp = 0x0002;

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
