# Generates src/Windshot/Assets/Windshot.ico.
# Each size is drawn on its own (not downscaled from a big one) so small sizes stay crisp.
# Usage: powershell -File tools/make-icon.ps1 [-Preview <png path>]
param([string]$Preview)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\icon-drawing.ps1"

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$out = Join-Path $PSScriptRoot '..\src\Windshot\Assets\Windshot.ico'

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
