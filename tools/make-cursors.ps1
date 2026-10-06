# Generates Windshot's own cursors in src/Windshot/Assets, sized and outlined to sit with
# Windows' arrow (about 17 px tall in a 32 px cursor, with a 1 px outline):
#   hand-open.cur  ready to drag (panning, pins)       white, 1 px dark outline
#   hand-grab.cur  dragging                            the same hand, fingers folded down
#   rotate.cur     over the rotate handle              a clockwise arrow, same style
#   cross.cur      drawing                             a thin black cross with a white edge
#                                                      and a gap at the hotspot
# Windows has no hand or rotate cursor besides the link pointer, and its precision-select
# cross is heavier than the arrow. The hand and arrow are Fluent System Icons glyphs (MIT).
# Each file holds 32, 48 and 64 px images, for 100%, 150% and 200% scaling.
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

$face = New-Object Windows.Media.GlyphTypeface (New-Object Uri (Join-Path $FontDir 'FluentSystemIcons-Filled.ttf'))
$map = Get-Content (Join-Path $FontDir 'FluentSystemIcons-Filled.json') -Raw | ConvertFrom-Json
function Glyph([string]$name) {
    # On a 24-unit grid, with its top at 0.
    $outline = $face.GetGlyphOutline($face.CharacterToGlyphMap[[int]$map."ic_fluent_${name}_24_filled"], 24, 24)
    [Windows.Media.Geometry]::Combine($outline, $outline, 'Union', (New-Object Windows.Media.TranslateTransform 0, ($face.Baseline * 24)))
}

$open = Glyph 'hand_left'
$box = $open.Bounds
# Grabbing: the finger part (above the palm) squashed to 40% of its height and lowered onto the palm.
$palmTop = $box.Top + $box.Height * 0.5
$fingers = [Windows.Media.Geometry]::Combine($open, (New-Object Windows.Media.RectangleGeometry (New-Object Windows.Rect $box.Left, $box.Top, $box.Width, ($palmTop - $box.Top + 0.6))), 'Intersect', $null)
$palm = [Windows.Media.Geometry]::Combine($open, (New-Object Windows.Media.RectangleGeometry (New-Object Windows.Rect $box.Left, $palmTop, $box.Width, ($box.Bottom - $palmTop))), 'Intersect', $null)
$fold = New-Object Windows.Media.TransformGroup
$fold.Children.Add((New-Object Windows.Media.ScaleTransform 1, 0.4, 0, $palmTop))
$folded = [Windows.Media.Geometry]::Combine($fingers, $fingers, 'Union', $fold)
$grab = [Windows.Media.Geometry]::Combine($folded, $palm, 'Union', $null)
$rotate = Glyph 'arrow_clockwise'

# Renders a shape centered in a size x size cursor. $extent is how many pixels (at 32 px)
# the reference box's larger side spans; both hands use the open hand's box, so the grabbing
# hand is the same size rather than blown up to fill the cursor.
function Render-Icon([Windows.Media.Geometry]$shape, [Windows.Rect]$reference, [double]$extent, [int]$size) {
    $b = $shape.Bounds
    $scale = ($extent * $size / 32) / [Math]::Max($reference.Width, $reference.Height)
    $group = New-Object Windows.Media.TransformGroup
    $group.Children.Add((New-Object Windows.Media.TranslateTransform (-$b.Left - $b.Width / 2), (-$b.Top - $b.Height / 2)))
    $group.Children.Add((New-Object Windows.Media.ScaleTransform $scale, $scale))
    $group.Children.Add((New-Object Windows.Media.TranslateTransform ($size / 2), ($size / 2)))
    $placed = $shape.Clone(); $placed.Transform = $group

    $visual = New-Object Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    # The outline goes behind the white, so half of it shows: a whole pixel (two at 64 px), like the arrow's.
    $pen = New-Object Windows.Media.Pen ([Windows.Media.Brushes]::Black), (2 * [Math]::Max(1, [Math]::Floor($size / 32)))
    $pen.LineJoin = 'Round'
    $dc.DrawGeometry($null, $pen, $placed)
    $dc.DrawGeometry([Windows.Media.Brushes]::White, $null, $placed)
    $dc.Close()
    Pixels $visual $size
}

# A thin cross: black lines one pixel wide (two at 64 px) with a one-pixel white edge, and a
# gap in the middle so the pixel under the hotspot stays visible.
function Render-Cross([int]$size) {
    $line = [Math]::Max(1, [Math]::Floor($size / 32))
    $c = $size / 2 + ($line % 2) / 2   # on a pixel center for odd widths, a pixel edge for even
    $k = $size / 32
    $gap = [Math]::Round(3 * $k); $reach = [Math]::Round(9 * $k)
    $arms = New-Object Windows.Media.GeometryGroup
    foreach ($r in @(@(($c - $reach), ($c - $gap)), @(($c + $gap), ($c + $reach)))) {
        $arms.Children.Add((New-Object Windows.Media.RectangleGeometry (New-Object Windows.Rect $r[0], ($c - $line / 2), ($r[1] - $r[0]), $line)))
        $arms.Children.Add((New-Object Windows.Media.RectangleGeometry (New-Object Windows.Rect ($c - $line / 2), $r[0], $line, ($r[1] - $r[0]))))
    }
    $visual = New-Object Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $edge = New-Object Windows.Media.Pen ([Windows.Media.Brushes]::White), (2 * $line)
    $dc.DrawGeometry($null, $edge, $arms)
    $dc.DrawGeometry([Windows.Media.Brushes]::Black, $null, $arms)
    $dc.Close()
    Pixels $visual $size
}

function Pixels([Windows.Media.Visual]$visual, [int]$size) {
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $pixels = New-Object byte[] ($size * $size * 4)
    $bitmap.CopyPixels($pixels, $size * 4, 0)
    # Cursor bitmaps take straight alpha.
    for ($i = 0; $i -lt $pixels.Length; $i += 4) {
        $a = $pixels[$i + 3]
        if ($a -gt 0 -and $a -lt 255) { for ($ch = 0; $ch -lt 3; $ch++) { $pixels[$i + $ch] = [byte][Math]::Min(255, [Math]::Round($pixels[$i + $ch] * 255.0 / $a)) } }
    }
    return , $pixels
}

# A .cur file: a directory of images, each a 32-bit DIB (bottom-up, height doubled for the
# unused AND mask) with its hotspot, here the middle.
function Write-Cursor([scriptblock]$render, [string]$path) {
    $sizes = 32, 48, 64
    $images = foreach ($size in $sizes) {
        $pixels = & $render $size
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
Write-Cursor { param($s) Render-Icon $open $box 18 $s } (Join-Path $assets 'hand-open.cur')
Write-Cursor { param($s) Render-Icon $grab $box 18 $s } (Join-Path $assets 'hand-grab.cur')
Write-Cursor { param($s) Render-Icon $rotate $rotate.Bounds 16 $s } (Join-Path $assets 'rotate.cur')
Write-Cursor { param($s) Render-Cross $s } (Join-Path $assets 'cross.cur')
