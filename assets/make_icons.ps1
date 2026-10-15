# Draws ZoomScroller's icons from the tray's magnifier (DrawIcon in src\Program.cs): assets\zoomscroller.ico for the exe
# and extension\icons\icon*.png for the helper extension. Run it again after changing the drawing.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Set-Location (Split-Path $PSScriptRoot)

# The magnifier on a 16-unit grid, drawn inside size - 2 * pad pixels.
function Draw([int]$size, [int]$pad) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $c = [System.Drawing.Color]::FromArgb(0, 150, 255)
    $u = ($size - 2 * $pad) / 16.0
    $pen = New-Object System.Drawing.Pen $c, ([single](1.8 * $u))
    $handle = New-Object System.Drawing.Pen $c, ([single](2.6 * $u))
    $handle.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $handle.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $p = { param($v) [single]($pad + $v * $u) }
    $g.DrawEllipse($pen, (& $p 1.5), (& $p 1.5), [single](9 * $u), [single](9 * $u))
    $g.DrawLine($handle, (& $p 9.6), (& $p 9.6), (& $p 14.2), (& $p 14.2))
    $g.DrawLine($pen, (& $p 3.8), (& $p 6), (& $p 8.2), (& $p 6))
    $g.DrawLine($pen, (& $p 6), (& $p 3.8), (& $p 6), (& $p 8.2))
    $pen.Dispose(); $handle.Dispose(); $g.Dispose()
    return $bmp
}

function Png($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$ms.ToArray()
}

# An icon image as a 32-bit DIB: BITMAPINFOHEADER (double height), BGRA rows bottom-up, then the 1-bit AND mask.
function Dib($bmp) {
    $s = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([int]40); $w.Write([int]$s); $w.Write([int](2 * $s)); $w.Write([int16]1); $w.Write([int16]32)
    for ($i = 0; $i -lt 6; $i++) { $w.Write([int]0) }
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
    $mask = New-Object byte[] ($maskRow * $s)
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
            if ($c.A -eq 0) {
                $at = ($s - 1 - $y) * $maskRow + [int][Math]::Floor($x / 8)
                $mask[$at] = [byte]($mask[$at] -bor (0x80 -shr ($x % 8)))
            }
        }
    }
    $w.Write($mask)
    $w.Flush()
    return ,$ms.ToArray()
}

New-Item -ItemType Directory -Force extension\icons | Out-Null
# Chrome's guideline for the 128 px icon: 96 px of artwork with 16 px of clear space around it
foreach ($e in @(@(16, 0), @(32, 1), @(48, 3), @(128, 16))) {
    $bmp = Draw $e[0] $e[1]
    $bmp.Save("extension\icons\icon$($e[0]).png", [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($s in $sizes) {
    $bmp = Draw $s ([int][Math]::Floor($s / 32))
    if ($s -ge 256) { ,(Png $bmp) } else { ,(Dib $bmp) }
    $bmp.Dispose()
}
$fs = [System.IO.File]::Create((Join-Path (Get-Location) "assets\zoomscroller.ico"))
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = [byte]($s % 256)
    $w.Write($b); $w.Write($b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$images[$i].Length); $w.Write([int]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
"wrote extension\icons\icon{16,32,48,128}.png and assets\zoomscroller.ico"
