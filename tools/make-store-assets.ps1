# Generates the MSIX package logos in src/Windshot/Assets/Package from the app icon drawing.
# Usage: powershell -File tools/make-store-assets.ps1
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\icon-drawing.ps1"

$out = Join-Path $PSScriptRoot '..\src\Windshot\Assets\Package'
New-Item -ItemType Directory -Force $out | Out-Null

# The icon, drawn at its own size and centered on a transparent canvas of the given size.
function Save-Logo([string]$name, [int]$width, [int]$height, [double]$iconFraction) {
    $icon = Draw-Icon ([int][Math]::Round([Math]::Min($width, $height) * $iconFraction))
    $canvas = New-Object Drawing.Bitmap $width, $height, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($canvas)
    $g.DrawImageUnscaled($icon, [int](($width - $icon.Width) / 2), [int](($height - $icon.Height) / 2))
    $g.Dispose(); $icon.Dispose()
    $canvas.Save((Join-Path $out $name), [Drawing.Imaging.ImageFormat]::Png)
    $canvas.Dispose()
}

$scales = @{ 100 = 1.0; 125 = 1.25; 150 = 1.5; 200 = 2.0; 400 = 4.0 }
foreach ($scale in $scales.Keys) {
    $f = $scales[$scale]
    # Start menu, taskbar and Settings: the icon fills the square, like the .ico.
    Save-Logo "Square44x44Logo.scale-$scale.png" ([int](44 * $f)) ([int](44 * $f)) 1.0
    # Tiles and the splash leave room around the icon.
    Save-Logo "Square150x150Logo.scale-$scale.png" ([int](150 * $f)) ([int](150 * $f)) 0.6
    Save-Logo "Wide310x150Logo.scale-$scale.png" ([int](310 * $f)) ([int](150 * $f)) 0.6
    Save-Logo "StoreLogo.scale-$scale.png" ([int](50 * $f)) ([int](50 * $f)) 1.0
    Save-Logo "SplashScreen.scale-$scale.png" ([int](620 * $f)) ([int](300 * $f)) 0.5
}
# Exact pixel sizes for the taskbar, Alt+Tab and file lists; "unplated" means drawn without
# a colored backplate, which this icon doesn't need.
foreach ($size in 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256) {
    Save-Logo "Square44x44Logo.targetsize-$size.png" $size $size 1.0
    Save-Logo "Square44x44Logo.altform-unplated_targetsize-$size.png" $size $size 1.0
    Save-Logo "Square44x44Logo.altform-lightunplated_targetsize-$size.png" $size $size 1.0
}
"Wrote $((Get-ChildItem $out -Filter *.png).Count) logos to $((Resolve-Path $out).Path)"
