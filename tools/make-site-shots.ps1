# Makes the website's screenshots (site/shots) from the Store screenshots: the headline band is
# cropped off (the page has its own captions) and each is saved as a JPEG at two widths.
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot
$src = Join-Path $root 'store\screenshots'
$out = Join-Path $root 'site\shots'
New-Item -ItemType Directory -Force $out | Out-Null

$jpeg = [Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq 'image/jpeg'
$params = New-Object Drawing.Imaging.EncoderParameters 1
$params.Param[0] = New-Object Drawing.Imaging.EncoderParameter ([Drawing.Imaging.Encoder]::Quality), 88L

# Below the headline, down to just under the shadow of the tallest window.
$crop = New-Object Drawing.Rectangle 0, 330, 3840, 1780

foreach ($file in Get-ChildItem $src -Filter '0*.png') {
  $img = [Drawing.Image]::FromFile($file.FullName)
  foreach ($width in 960, 1920) {
    $height = [int]($crop.Height * $width / $crop.Width)
    $bmp = New-Object Drawing.Bitmap $width, $height
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.SmoothingMode = 'HighQuality'
    $g.DrawImage($img, (New-Object Drawing.Rectangle 0, 0, $width, $height), $crop, [Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    $path = Join-Path $out "$($file.BaseName)-$width.jpg"
    $bmp.Save($path, $jpeg, $params); $bmp.Dispose()
    '{0} {1:N0} KB' -f (Split-Path $path -Leaf), ((Get-Item $path).Length / 1KB)
  }
  $img.Dispose()
}
