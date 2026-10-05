# The Windshot icon drawing, shared by make-icon.ps1 (the .ico) and make-store-assets.ps1 (package logos).
# Each size is drawn on its own (not downscaled from a big one) so small sizes stay crisp.
Add-Type -AssemblyName System.Drawing

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
