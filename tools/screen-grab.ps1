# Captures a region of the screen including layered windows (CAPTUREBLT) - used for live UI checks.
# Usage: screen-grab.ps1 -Out shot.png [-X 0 -Y 0 -W 1200 -H 400] [-MoveMouseX 960 -MoveMouseY 12]
param(
    [Parameter(Mandatory)] [string]$Out,
    [int]$X = -1, [int]$Y = 0, [int]$W = 1400, [int]$H = 420,
    [int]$MoveMouseX = -1, [int]$MoveMouseY = -1, [int]$DelayMs = 0
)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Grab {
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int rop);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
}
"@
[Grab]::SetProcessDpiAwarenessContext([IntPtr]-4) | Out-Null # per-monitor v2: real pixels on every display
$screenW = [Grab]::GetSystemMetrics(0)
if ($X -lt 0) { $X = [int](($screenW - $W) / 2) }
if ($MoveMouseX -ge 0) { [Grab]::SetCursorPos($MoveMouseX, $MoveMouseY) | Out-Null }
if ($DelayMs -gt 0) { Start-Sleep -Milliseconds $DelayMs }
$bmp = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($bmp)
$dst = $g.GetHdc(); $src = [Grab]::GetDC([IntPtr]::Zero)
[Grab]::BitBlt($dst, 0, 0, $W, $H, $src, $X, $Y, 0x40CC0020) | Out-Null
[Grab]::ReleaseDC([IntPtr]::Zero, $src) | Out-Null
$g.ReleaseHdc($dst); $g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
"$Out ($X,$Y ${W}x$H, screen width $screenW)"
