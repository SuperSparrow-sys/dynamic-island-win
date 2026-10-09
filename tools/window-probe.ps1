# Diagnostics: lists all top-level windows of DynamicBay and the state of every taskbar (primary + secondary).
# Usage: powershell -File tools\window-probe.ps1
Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class Probe {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder b, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder b, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public static List<string> List(uint pid) {
        var res = new List<string>();
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            var cls = new StringBuilder(256); GetClassName(h, cls, 256);
            string c = cls.ToString();
            if (p == pid || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") {
                var t = new StringBuilder(256); GetWindowText(h, t, 256);
                RECT r; GetWindowRect(h, out r);
                long ex = GetWindowLongPtr(h, -20).ToInt64();
                res.Add(string.Format("{0,-28} '{1}' visible={2} rect=({3},{4})-({5},{6}) topmost={7} exstyle=0x{8:X}",
                    c, t, IsWindowVisible(h), r.L, r.T, r.R, r.B, (ex & 8) != 0, ex));
            }
            return true;
        }, IntPtr.Zero);
        return res;
    }
}
"@
[Probe]::SetProcessDpiAwarenessContext([IntPtr]-4) | Out-Null # per-monitor v2: real pixels on every display
$p = Get-Process DynamicBay -ErrorAction SilentlyContinue | Select-Object -First 1
$procId = if ($p) { [uint32]$p.Id } else { [uint32]0 }
[Probe]::List($procId)
