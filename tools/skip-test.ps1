# Presses "next track" and captures the island every 150 ms for 3 s - verifies there is no "not playing" flash.
# Usage: powershell -File tools\skip-test.ps1 -Out <dir> [-X 660 -Y 0 -W 600 -H 70]
param([Parameter(Mandatory)] [string]$Out, [int]$X = 660, [int]$Y = 0, [int]$W = 600, [int]$H = 70, [int]$Frames = 20)
New-Item -ItemType Directory -Force $Out | Out-Null
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class St {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int rop);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
}
"@
[St]::SetProcessDpiAwarenessContext([IntPtr]-4) | Out-Null
function Grab($i) {
    $bmp = New-Object System.Drawing.Bitmap $W, $H
    $g = [System.Drawing.Graphics]::FromImage($bmp); $d = $g.GetHdc(); $s = [St]::GetDC([IntPtr]::Zero)
    [St]::BitBlt($d, 0, 0, $W, $H, $s, $X, $Y, 0x40CC0020) | Out-Null
    [St]::ReleaseDC([IntPtr]::Zero, $s) | Out-Null; $g.ReleaseHdc($d); $g.Dispose()
    $bmp.Save((Join-Path $Out ("f{0:00}.png" -f $i))); $bmp.Dispose()
}
Grab 0
[St]::keybd_event(0xB0, 0, 0, [IntPtr]::Zero); [St]::keybd_event(0xB0, 0, 2, [IntPtr]::Zero)
for ($i = 1; $i -le $Frames; $i++) { Start-Sleep -Milliseconds 150; Grab $i }
"captured $($Frames + 1) frames"
