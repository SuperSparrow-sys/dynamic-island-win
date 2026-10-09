# Diagnostics: logs every change of the taskbars' visibility/position (incl. the secondary monitor) for N seconds.
# Usage: powershell -File tools\taskbar-watch.ps1 -Seconds 60 -Out taskbar.log
param([int]$Seconds = 60, [string]$Out = "taskbar-watch.log")
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class Tb {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder b, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public static string State() {
        var sb = new StringBuilder();
        EnumWindows((h, l) => {
            var c = new StringBuilder(64); GetClassName(h, c, 64);
            string cls = c.ToString();
            if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") {
                RECT r; GetWindowRect(h, out r);
                sb.AppendFormat("{0}:{1}:({2},{3})-({4},{5}) ", cls == "Shell_TrayWnd" ? "main" : "second", IsWindowVisible(h) ? "vis" : "HIDDEN", r.L, r.T, r.R, r.B);
            }
            return true;
        }, IntPtr.Zero);
        var fg = GetForegroundWindow(); uint pid; GetWindowThreadProcessId(fg, out pid);
        var fc = new StringBuilder(64); GetClassName(fg, fc, 64);
        sb.AppendFormat("| fg={0} pid={1}", fc, pid);
        return sb.ToString();
    }
}
"@
[Tb]::SetProcessDpiAwarenessContext([IntPtr]-4) | Out-Null # per-monitor v2: real pixels on every display
$last = ""; $end = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $end) {
    $s = [Tb]::State()
    # compare without the foreground part so only taskbar changes are logged (plus fg at that moment)
    $key = $s.Split('|')[0]
    if ($key -ne $last) { "$(Get-Date -Format HH:mm:ss.fff) $s" | Tee-Object -FilePath $Out -Append; $last = $key }
    Start-Sleep -Milliseconds 150
}
