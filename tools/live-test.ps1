# End-to-end UI smoke test against the real running app (mouse hover, screenshot peek, drag to edge).
# Usage: powershell -File tools\live-test.ps1 -Out <dir>   (the app must not be running; it is started by the script)
param([string]$Out = "$PSScriptRoot\..\artifacts\live", [string]$Configuration = "Debug")
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $Out | Out-Null
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Ui {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int rop);
}
"@
[Ui]::SetProcessDPIAware() | Out-Null
$sw = [Ui]::GetSystemMetrics(0); $sh = [Ui]::GetSystemMetrics(1)

function Grab($name, $x, $y, $w, $h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $d = $g.GetHdc(); $s = [Ui]::GetDC([IntPtr]::Zero)
    [Ui]::BitBlt($d, 0, 0, $w, $h, $s, $x, $y, 0x40CC0020) | Out-Null
    [Ui]::ReleaseDC([IntPtr]::Zero, $s) | Out-Null; $g.ReleaseHdc($d); $g.Dispose()
    $bmp.Save((Join-Path $Out "$name.png")); $bmp.Dispose()
    Write-Output "captured $name"
}
function Drag($x1, $y1, $x2, $y2) {
    [Ui]::SetCursorPos($x1, $y1) | Out-Null; Start-Sleep -Milliseconds 300
    [Ui]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero) # left down
    for ($i = 1; $i -le 30; $i++) {
        [Ui]::SetCursorPos([int]($x1 + ($x2 - $x1) * $i / 30), [int]($y1 + ($y2 - $y1) * $i / 30)) | Out-Null
        Start-Sleep -Milliseconds 15
    }
    [Ui]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero) # left up
}

Get-Process DynamicBay -ErrorAction SilentlyContinue | Stop-Process -Force
$settings = Join-Path $env:APPDATA "DynamicBay\settings.json"
if (Test-Path $settings) { Copy-Item $settings "$settings.bak" -Force; Remove-Item $settings }
$exe = (Get-ChildItem "$PSScriptRoot\..\src\DynamicBay\bin\$Configuration" -Recurse -Filter DynamicBay.exe | Select-Object -First 1).FullName
Start-Process $exe
[Ui]::SetCursorPos(200, [int]($sh * 0.7)) | Out-Null
Start-Sleep 4
$cx = [int]($sw / 2)
Grab "01-idle" ($cx - 450) 0 900 160

[Ui]::SetCursorPos($cx, 14) | Out-Null; Start-Sleep -Milliseconds 1300
Grab "02-expanded" ($cx - 500) 0 1000 380
[Ui]::SetCursorPos(200, [int]($sh * 0.7)) | Out-Null; Start-Sleep -Milliseconds 1500
Grab "03-collapsed-again" ($cx - 450) 0 900 160

# Screenshot peek: drop a PNG into the watched screenshots folder (removed again afterwards)
$shotDir = Join-Path ([Environment]::GetFolderPath("MyPictures")) "Screenshots"
if (-not (Test-Path $shotDir)) { $shotDir = Join-Path $env:OneDrive "Bilder\Screenshots" }
$test = Join-Path $shotDir "DynamicBay-Test $(Get-Date -Format HHmmss).png"
$bmp = New-Object System.Drawing.Bitmap 640, 360
$g = [System.Drawing.Graphics]::FromImage($bmp); $g.Clear([System.Drawing.Color]::FromArgb(255, 10, 132, 255))
$g.FillRectangle([System.Drawing.Brushes]::White, 40, 40, 300, 60); $g.Dispose()
$bmp.Save($test); $bmp.Dispose()
Start-Sleep -Milliseconds 1700
Grab "04-screenshot-peek" ($cx - 450) 0 900 160
Remove-Item $test -ErrorAction SilentlyContinue

# Drag to the left edge -> vertical orientation
Start-Sleep 3
Drag $cx 14 30 ([int]($sh / 2))
[Ui]::SetCursorPos(600, [int]($sh * 0.7)) | Out-Null
Start-Sleep -Milliseconds 1500
Grab "05-left-edge" 0 ([int]($sh / 2) - 350) 700 700
[Ui]::SetCursorPos(14, [int]($sh / 2)) | Out-Null; Start-Sleep -Milliseconds 1400
Grab "06-left-expanded" 0 ([int]($sh / 2) - 350) 700 700

# Drag to bottom center -> opens upward
[Ui]::SetCursorPos(600, [int]($sh * 0.7)) | Out-Null; Start-Sleep -Milliseconds 1200
Drag 14 ([int]($sh / 2)) $cx ($sh - 60)
[Ui]::SetCursorPos(200, 300) | Out-Null
Start-Sleep -Milliseconds 1500
Grab "07-bottom" ($cx - 500) ($sh - 420) 1000 420

Get-Process DynamicBay -ErrorAction SilentlyContinue | Stop-Process -Force
if (Test-Path "$settings.bak") { Move-Item "$settings.bak" $settings -Force } else { Remove-Item $settings -ErrorAction SilentlyContinue }
Write-Output "done"
