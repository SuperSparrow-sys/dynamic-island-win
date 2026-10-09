# Renders the DynamicBay app icon (gradient squircle + black island pill with a waveform) to a multi-size .ico.
# Usage: powershell -File make-app-icon.ps1 -Out ..\..\src\DynamicBay\Assets\DynamicBay.ico
param([string]$Out = "$PSScriptRoot\..\..\src\DynamicBay\Assets\DynamicBay.ico")
Add-Type -AssemblyName System.Drawing

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $s = $size / 256.0
    $m = 8 * $s
    $bg = New-RoundRect $m $m ($size - 2 * $m) ($size - 2 * $m) (58 * $s)
    $rect = New-Object System.Drawing.RectangleF 0, 0, $size, $size
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 64, 156, 255)), ([System.Drawing.Color]::FromArgb(255, 136, 84, 255)), 60
    $g.FillPath($grad, $bg)
    # soft highlight
    $hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(70, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(0, 255, 255, 255)), 90
    $g.FillPath($hl, $bg)

    # island pill
    $pw = 168 * $s; $ph = 58 * $s
    $px = ($size - $pw) / 2; $py = 52 * $s
    $pill = New-RoundRect $px $py $pw $ph ($ph / 2)
    $g.FillPath([System.Drawing.Brushes]::Black, $pill)

    # waveform bars (green) on the right, music dot on the left
    $green = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 48, 209, 88))
    $heights = @(18, 32, 24, 36)
    $bw = 8 * $s; $gap = 6 * $s
    $bx = $px + $pw - 26 * $s - (4 * $bw + 3 * $gap)
    for ($i = 0; $i -lt 4; $i++) {
        $bh = $heights[$i] * $s
        $bar = New-RoundRect ($bx + $i * ($bw + $gap)) ($py + ($ph - $bh) / 2) $bw $bh ($bw / 2)
        $g.FillPath($green, $bar)
    }
    $dot = 22 * $s
    $g.FillEllipse([System.Drawing.Brushes]::White, $px + 22 * $s, $py + ($ph - $dot) / 2, $dot, $dot)

    # lower "panel" hint lines
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 255, 255, 255))
    $soft = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(110, 255, 255, 255))
    if ($size -ge 32) {
        $g.FillPath($white, (New-RoundRect (52 * $s) (146 * $s) (152 * $s) (16 * $s) (8 * $s)))
        $g.FillPath($soft, (New-RoundRect (52 * $s) (176 * $s) (104 * $s) (16 * $s) (8 * $s)))
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($sz in $sizes) {
    $b = Render $sz
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , @($sz, $ms.ToArray())
    if ($sz -eq 256) { $b.Save([System.IO.Path]::ChangeExtension($Out, ".png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $b.Dispose()
}

# ICO container with PNG-compressed entries
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $sz = $p[0]; $data = $p[1]
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($p in $pngs) { $bw.Write([byte[]]$p[1]) }
$bw.Close()
Write-Output "wrote $Out"
