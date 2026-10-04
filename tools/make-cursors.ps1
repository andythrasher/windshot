# Generates the pan cursors in src/Windshot/Assets: hand-open.cur (ready to drag) and
# hand-grab.cur (dragging). Windows has no hand cursors besides the link pointer, so these are
# drawn from the Fluent System Icons hand (MIT): white with a dark outline like the system
# cursors, at 32, 48 and 64 px for 100%, 150% and 200% scaling. The grabbing hand is the
# same glyph with the fingers folded down.
#
# Usage (Windows PowerShell 5.1, which has WPF):
#   powershell -STA -File tools/make-cursors.ps1 [-FontDir <folder with the Fluent fonts>]
# Fonts are downloaded into -FontDir if missing, as in make-fluent-icons.ps1.
param([string]$FontDir = (Join-Path $env:TEMP 'windshot-fluent-icons'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

New-Item -ItemType Directory -Force $FontDir | Out-Null
$base = 'https://raw.githubusercontent.com/microsoft/fluentui-system-icons/main/fonts'
foreach ($file in 'FluentSystemIcons-Filled.ttf', 'FluentSystemIcons-Filled.json') {
    $path = Join-Path $FontDir $file
    if (-not (Test-Path $path)) { Invoke-WebRequest "$base/$file" -OutFile $path -UseBasicParsing }
}

# The hand on a 24-unit grid, with its top at 0.
$face = New-Object Windows.Media.GlyphTypeface (New-Object Uri (Join-Path $FontDir 'FluentSystemIcons-Filled.ttf'))
$map = Get-Content (Join-Path $FontDir 'FluentSystemIcons-Filled.json') -Raw | ConvertFrom-Json
$glyph = $face.GetGlyphOutline($face.CharacterToGlyphMap[[int]$map.ic_fluent_hand_left_24_filled], 24, 24)
$open = [Windows.Media.Geometry]::Combine($glyph, $glyph, 'Union', (New-Object Windows.Media.TranslateTransform 0, ($face.Baseline * 24)))
$box = $open.Bounds

# Grabbing: the finger part (above the palm) squashed to 40% of its height and lowered onto the palm.
$palmTop = $box.Top + $box.Height * 0.5
$fingers = [Windows.Media.Geometry]::Combine($open, (New-Object Windows.Media.RectangleGeometry (New-Object Windows.Rect $box.Left, $box.Top, $box.Width, ($palmTop - $box.Top + 0.6))), 'Intersect', $null)
$palm = [Windows.Media.Geometry]::Combine($open, (New-Object Windows.Media.RectangleGeometry (New-Object Windows.Rect $box.Left, $palmTop, $box.Width, ($box.Bottom - $palmTop))), 'Intersect', $null)
$fold = New-Object Windows.Media.TransformGroup
$fold.Children.Add((New-Object Windows.Media.ScaleTransform 1, 0.4, 0, $palmTop))
$folded = [Windows.Media.Geometry]::Combine($fingers, $fingers, 'Union', $fold)
$grab = [Windows.Media.Geometry]::Combine($folded, $palm, 'Union', $null)

function Render([Windows.Media.Geometry]$shape, [int]$size) {
    # Fit the shape into the cursor with room for the outline, centered.
    $b = $shape.Bounds
    $scale = ($size * 0.84) / [Math]::Max($b.Width, $b.Height)
    $group = New-Object Windows.Media.TransformGroup
    $group.Children.Add((New-Object Windows.Media.TranslateTransform (-$b.Left - $b.Width / 2), (-$b.Top - $b.Height / 2)))
    $group.Children.Add((New-Object Windows.Media.ScaleTransform $scale, $scale))
    $group.Children.Add((New-Object Windows.Media.TranslateTransform ($size / 2), ($size / 2)))
    $placed = $shape.Clone(); $placed.Transform = $group

    $visual = New-Object Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $pen = New-Object Windows.Media.Pen ([Windows.Media.Brushes]::Black), ([Math]::Max(1.5, $size / 20))
    $pen.LineJoin = 'Round'
    $dc.DrawGeometry([Windows.Media.Brushes]::White, $pen, $placed)
    $dc.Close()
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $pixels = New-Object byte[] ($size * $size * 4)
    $bitmap.CopyPixels($pixels, $size * 4, 0)
    # Cursor bitmaps take straight alpha.
    for ($i = 0; $i -lt $pixels.Length; $i += 4) {
        $a = $pixels[$i + 3]
        if ($a -gt 0 -and $a -lt 255) { for ($c = 0; $c -lt 3; $c++) { $pixels[$i + $c] = [byte][Math]::Min(255, [Math]::Round($pixels[$i + $c] * 255.0 / $a)) } }
    }
    return , $pixels
}

# A .cur file: a directory of images, each a 32-bit DIB (bottom-up, height doubled for the
# unused AND mask) with its hotspot, here the middle of the palm.
function Write-Cursor([Windows.Media.Geometry]$shape, [string]$path) {
    $sizes = 32, 48, 64
    $images = foreach ($size in $sizes) {
        $pixels = Render $shape $size
        $stream = New-Object IO.MemoryStream
        $w = New-Object IO.BinaryWriter $stream
        $w.Write([int]40); $w.Write([int]$size); $w.Write([int]($size * 2)); $w.Write([int16]1); $w.Write([int16]32)
        $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
        for ($y = $size - 1; $y -ge 0; $y--) { $w.Write($pixels, $y * $size * 4, $size * 4) }
        $w.Write((New-Object byte[] ([Math]::Ceiling($size / 32) * 4 * $size)))
        $w.Flush()
        , $stream.ToArray()
    }
    $out = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $out
    $w.Write([int16]0); $w.Write([int16]2); $w.Write([int16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]
        $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([int16]($s / 2)); $w.Write([int16]($s / 2))
        $w.Write([int]$images[$i].Length); $w.Write([int]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $w.Write($image) }
    $w.Flush()
    [IO.File]::WriteAllBytes($path, $out.ToArray())
    "Wrote $path ($($out.Length) bytes)"
}

$assets = Join-Path $PSScriptRoot '..\src\Windshot\Assets'
Write-Cursor $open (Join-Path $assets 'hand-open.cur')
Write-Cursor $grab (Join-Path $assets 'hand-grab.cur')
