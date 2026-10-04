# Generates src/Windshot/Assets/Windshot.ico.
# Each size is drawn on its own (not downscaled from a big one) so small sizes stay crisp.
# Usage: powershell -File tools/make-icon.ps1 [-Preview <png path>]
param([string]$Preview)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$out = Join-Path $PSScriptRoot '..\src\Windshot\Assets\Windshot.ico'

function Draw-Icon([int]$s) {
    $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'

    # Rounded square with the palette's ocean-to-lavender gradient.
    $inset = [Math]::Max(0.5, $s * 0.04)
    $box = New-Object Drawing.RectangleF $inset, $inset, ($s - 2 * $inset), ($s - 2 * $inset)
    $r = $s * 0.22
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc($box.X, $box.Y, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($box.Right - 2 * $r, $box.Y, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($box.Right - 2 * $r, $box.Bottom - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($box.X, $box.Bottom - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $ocean = [Drawing.Color]::FromArgb(255, 64, 132, 214)
    $lavender = [Drawing.Color]::FromArgb(255, 150, 110, 205)
    $fill = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.PointF 0, 0), (New-Object Drawing.PointF $s, $s), $ocean, $lavender
    $g.FillPath($fill, $path)

    # White capture brackets in the four corners; thicker strokes at small sizes.
    $w = if ($s -le 24) { [Math]::Max(1.6, $s * 0.1) } else { $s * 0.075 }
    $pen = New-Object Drawing.Pen ([Drawing.Color]::White), $w
    $pen.StartCap = 'Square'; $pen.EndCap = 'Square'; $pen.LineJoin = 'Miter'
    $b = $s * 0.25; $a = $s * 0.17
    foreach ($corner in @(@($b, $b, 1, 1), @(($s - $b), $b, -1, 1), @($b, ($s - $b), 1, -1), @(($s - $b), ($s - $b), -1, -1))) {
        $x, $y, $dx, $dy = $corner
        $g.DrawLines($pen, [Drawing.PointF[]]@(
            (New-Object Drawing.PointF $x, ($y + $dy * $a)),
            (New-Object Drawing.PointF $x, $y),
            (New-Object Drawing.PointF ($x + $dx * $a), $y)))
    }

    # A gust of wind through the middle, from 24 px up (too fussy below that).
    if ($s -ge 24) {
        $swoosh = New-Object Drawing.Pen ([Drawing.Color]::White), ($w * 0.9)
        $swoosh.StartCap = 'Round'; $swoosh.EndCap = 'Round'
        $g.DrawBezier($swoosh,
            (New-Object Drawing.PointF ($s * 0.36), ($s * 0.56)),
            (New-Object Drawing.PointF ($s * 0.46), ($s * 0.40)),
            (New-Object Drawing.PointF ($s * 0.54), ($s * 0.64)),
            (New-Object Drawing.PointF ($s * 0.66), ($s * 0.46)))
    }

    $g.Dispose()
    return $bmp
}

# Small sizes as 32-bit BMP entries (most compatible), 256 as PNG (what Windows expects).
$images = foreach ($s in $sizes) {
    $bmp = Draw-Icon $s
    if ($s -ge 256) {
        $ms = New-Object IO.MemoryStream
        $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray()
    } else {
        $data = $bmp.LockBits((New-Object Drawing.Rectangle 0, 0, $s, $s), 'ReadOnly', 'Format32bppArgb')
        $pixels = New-Object byte[] ($s * $s * 4)
        [Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
        $bmp.UnlockBits($data)
        $maskRow = [Math]::Ceiling($s / 32) * 4
        $ms = New-Object IO.MemoryStream
        $bw = New-Object IO.BinaryWriter $ms
        # BITMAPINFOHEADER; height is doubled to cover the (empty) AND mask.
        $bw.Write([int]40); $bw.Write([int]$s); $bw.Write([int]($s * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
        $bw.Write([int]0); $bw.Write([int]($pixels.Length + $maskRow * $s)); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
        for ($y = $s - 1; $y -ge 0; $y--) { $bw.Write($pixels, $y * $s * 4, $s * 4) }   # bottom-up rows
        $bw.Write((New-Object byte[] ($maskRow * $s)))
        $bw.Flush()
        $bytes = $ms.ToArray()
    }
    if ($Preview -and $s -eq 256) { $bmp.Save($Preview, [Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , @($s, $bytes)
}

New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
$fs = [IO.File]::Create($out)
$w = New-Object IO.BinaryWriter $fs
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $s, $bytes = $img
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$bytes.Length); $w.Write([int]$offset)
    $offset += $bytes.Length
}
foreach ($img in $images) { $w.Write($img[1]) }
$w.Close()
"Wrote $out ($((Get-Item $out).Length) bytes, sizes $($sizes -join ', '))"
