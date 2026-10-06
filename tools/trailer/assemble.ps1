# Cuts the recording from record.ps1 into the Store trailer, styled like the Store screenshots:
# the brand gradient, a headline per scene, and the recording inset with rounded corners and a
# shadow; a title card first and an end card last, over the music. Also makes the thumbnail.
#
#   powershell -File tools/trailer/assemble.ps1
#
# Output in artifacts\trailer: Windshot-trailer.mp4 (1920x1080, 30 fps, H.264 + AAC) and
# Windshot-trailer-thumbnail.png. The music is the track in store\trailer, from 0:34 to its
# stop at 1:20, so every cut lands on a bar line (120 BPM, a bar every 2 seconds) and the
# end card plays over the chorus.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$dir = Join-Path $root 'artifacts\trailer'
$build = Join-Path $dir 'build'
New-Item -ItemType Directory -Force $build | Out-Null
$ffmpeg = Get-ChildItem 'C:\dev\tools\ffmpeg' -Recurse -Filter ffmpeg.exe | Select-Object -First 1 -ExpandProperty FullName
$music = Get-ChildItem (Join-Path $root 'store\trailer') -File | Where-Object Extension -in '.mp3', '.wav', '.m4a' | Select-Object -First 1 -ExpandProperty FullName
$recording = Join-Path $dir 'recording.mkv'
$musicStart = 34

# Scenes: where they sit in the trailer, and what part of the recording fills them (seconds).
# Recording times come from marks.json, trimmed of idle moments; a scene's clip is sped up to
# fit its slot, or holds its last frame if it's short.
$scenes = @(
    @{ From = 2;  To = 6;  Start = 1.45;  End = 6.6;   Headline = 'Capture any part of your screen' },
    @{ From = 6;  To = 14; Start = 8.9;   End = 21.95; Headline = 'Annotate with arrows, text, steps and highlights' },
    @{ From = 14; To = 16; Start = 21.95; End = 25.4;  Headline = 'Blur anything private' },
    @{ From = 16; To = 26; Start = 25.64; End = 41.0;  Headline = 'Add pictures, and make anything stand out' },
    @{ From = 26; To = 32; Start = 41.13; End = 49.4;  Headline = 'Select several layers and change them together' },
    @{ From = 32; To = 36; Start = 49.53; End = 55.4;  Headline = 'Beautify in one click' },
    @{ From = 36; To = 40; Start = 56.3;  End = 57.9;  Headline = 'Saved straight to your Screenshots folder' }
)
$titleEnd = 2; $endCardStart = 40; $total = 46

# The recording's region of interest (16:9, inside the work area, clear of the taskbar), and
# where it sits in the frame.
$crop = 'crop=3000:1688:420:200'
$insetW = 1600; $insetH = 900; $insetX = 160; $insetY = 146

# ---- Pictures: background, mask, headlines, title and end cards --------------------------
function Gradient([int]$w, [int]$h) {
    $bmp = New-Object Drawing.Bitmap $w, $h
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.InterpolationMode = 'HighQualityBicubic'
    $brush = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.Point 0, 0), (New-Object Drawing.Point $w, $h), ([Drawing.Color]::FromArgb(255, 52, 102, 196)), ([Drawing.Color]::FromArgb(255, 128, 90, 196))
    $g.FillRectangle($brush, 0, 0, $w, $h)
    return $bmp, $g
}
function Rounded([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, 2*$r, 2*$r, 180, 90); $p.AddArc($x+$w-2*$r, $y, 2*$r, 2*$r, 270, 90)
    $p.AddArc($x+$w-2*$r, $y+$h-2*$r, 2*$r, 2*$r, 0, 90); $p.AddArc($x, $y+$h-2*$r, 2*$r, 2*$r, 90, 90); $p.CloseFigure()
    return $p
}
function Font([float]$size, [string]$style = 'Bold') {
    $f = New-Object Drawing.Font 'Segoe UI Variable Display', $size, ([Drawing.FontStyle]$style), ([Drawing.GraphicsUnit]::Pixel)
    if ($f.Name -ne 'Segoe UI Variable Display') { $f = New-Object Drawing.Font 'Segoe UI', $size, ([Drawing.FontStyle]$style), ([Drawing.GraphicsUnit]::Pixel) }
    return $f
}
function Centered($g, [string]$text, $font, [float]$y, [float]$h, $brush = [Drawing.Brushes]::White) {
    $fmt = New-Object Drawing.StringFormat; $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $g.DrawString($text, $font, $brush, (New-Object Drawing.RectangleF 0, $y, 1920, $h), $fmt)
}
function Shadow($g, [float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    for ($i = 14; $i -ge 1; $i--) {
        $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(6, 10, 10, 40))), (Rounded ($x - $i) ($y - $i + 10) ($w + 2 * $i) ($h + 2 * $i) ($r + $i)))
    }
}

# Background with the inset's shadow; the video goes on top.
$bmp, $g = Gradient 1920 1080
Shadow $g $insetX $insetY $insetW $insetH 14
$g.Dispose(); $bmp.Save("$build\background.png"); $bmp.Dispose()

# Rounded-corner mask for the inset.
$bmp = New-Object Drawing.Bitmap $insetW, $insetH
$g = [Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'; $g.Clear([Drawing.Color]::Black)
$g.FillPath([Drawing.Brushes]::White, (Rounded 0 0 $insetW $insetH 14))
$g.Dispose(); $bmp.Save("$build\mask.png"); $bmp.Dispose()

# A headline per scene, on transparency.
for ($i = 0; $i -lt $scenes.Count; $i++) {
    $bmp = New-Object Drawing.Bitmap 1920, 140
    $g = [Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    Centered $g $scenes[$i].Headline (Font 54) 10 120
    $g.Dispose(); $bmp.Save("$build\headline$i.png"); $bmp.Dispose()
}

# Title card: the icon, the name, the promise.
$icon = [Drawing.Image]::FromFile((Join-Path $root 'src\Windshot\Assets\Package\Square150x150Logo.scale-400.png'))
$bmp, $g = Gradient 1920 1080
$g.DrawImage($icon, 760, 170, 400, 400)
Centered $g 'Windshot' (Font 132) 560 170
Centered $g 'Screenshots for Windows 11, edited your way.' (Font 46 'Regular') 730 80
$g.Dispose(); $bmp.Save("$build\title.png"); $bmp.Dispose()

# End card.
$bmp, $g = Gradient 1920 1080
$g.DrawImage($icon, 810, 150, 300, 300)
Centered $g 'Windshot' (Font 120) 450 150
Centered $g 'Free on the Microsoft Store' (Font 54) 610 90
Centered $g 'windshot.app' (Font 40 'Regular') 700 70 (New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(220, 255, 255, 255)))
$g.Dispose(); $bmp.Save("$build\end.png"); $bmp.Dispose()

# Thumbnail: the finished, beautified capture from the recording, under the name.
$saved = Get-ChildItem "$dir\Screenshots" -Filter *.png | Sort-Object LastWriteTime | Select-Object -Last 1
$bmp, $g = Gradient 1920 1080
Centered $g 'Windshot' (Font 96) 40 130
if ($saved) {
    $shot = [Drawing.Image]::FromFile($saved.FullName)
    $s = [Math]::Min(1500 / $shot.Width, 820 / $shot.Height)
    $w = [int]($shot.Width * $s); $h = [int]($shot.Height * $s); $x = [int]((1920 - $w) / 2); $y = 200
    Shadow $g $x $y $w $h 12
    $clip = Rounded $x $y $w $h 12; $g.SetClip($clip); $g.DrawImage($shot, $x, $y, $w, $h); $g.ResetClip()
    $shot.Dispose()
}
$g.Dispose(); $bmp.Save("$dir\Windshot-trailer-thumbnail.png"); $bmp.Dispose()
$icon.Dispose()

# ---- The cut ---------------------------------------------------------------------------
$inputs = @('-i', $recording, '-loop', '1', '-framerate', '30', '-i', "$build\background.png", '-loop', '1', '-framerate', '30', '-i', "$build\mask.png",
            '-loop', '1', '-framerate', '30', '-t', $titleEnd, '-i', "$build\title.png",
            '-loop', '1', '-framerate', '30', '-t', ($total - $endCardStart), '-i', "$build\end.png",
            '-ss', $musicStart, '-t', $total, '-i', $music)
$first = 6   # headline inputs start here
foreach ($i in 0..($scenes.Count - 1)) { $inputs += @('-loop', '1', '-framerate', '30', '-i', "$build\headline$i.png") }

$f = New-Object Collections.Generic.List[string]
$names = @()
for ($i = 0; $i -lt $scenes.Count; $i++) {
    $sc = $scenes[$i]
    $slot = $sc.To - $sc.From
    $speed = [Math]::Max(1.0, ($sc.End - $sc.Start) / $slot)
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $f.Add(("[0:v]trim=start={0}:end={1},setpts=(PTS-STARTPTS)/{2},{3},scale={4}:{5}:flags=lanczos,fps=30,tpad=stop_mode=clone:stop_duration={6},trim=duration={6},setpts=PTS-STARTPTS,format=yuva420p[c{7}]" -f $sc.Start.ToString($inv), $sc.End.ToString($inv), $speed.ToString('0.0000', $inv), $crop, $insetW, $insetH, $slot, $i))
    $names += "[c$i]"
}
$f.Add(("{0}concat=n={1}:v=1:a=0[demo]" -f ($names -join ''), $scenes.Count))
$demoLength = $endCardStart - $titleEnd
$f.Add("[2:v]format=gray,trim=duration=$demoLength[mask]")
$f.Add("[demo][mask]alphamerge[rounded]")
$f.Add("[1:v]trim=duration=$demoLength,format=yuva420p[bg]")
$f.Add("[bg][rounded]overlay=$($insetX):$($insetY)[framed0]")
# Headlines, each fading in and out over its own scene.
$last = 'framed0'
for ($i = 0; $i -lt $scenes.Count; $i++) {
    $a = $scenes[$i].From - $titleEnd; $b = $scenes[$i].To - $titleEnd
    $f.Add(("[{0}:v]format=rgba,trim=duration={1},fade=in:st=0:d=0.25:alpha=1,fade=out:st={2}:d=0.2:alpha=1,setpts=PTS+{3}/TB[h{4}]" -f ($first + $i), ($b - $a), ($b - $a - 0.2), $a, $i))
    $f.Add(("[{0}][h{1}]overlay=0:0:eof_action=pass[framed{2}]" -f $last, $i, ($i + 1)))
    $last = "framed$($i + 1)"
}
$f.Add("[$last]format=yuv420p,setsar=1[demov]")
$f.Add("[3:v]format=yuv420p,setsar=1,fade=in:st=0:d=0.4[titlev]")
$f.Add("[4:v]format=yuv420p,setsar=1,fade=out:st=$($total - $endCardStart - 0.35):d=0.35[endv]")
$f.Add("[titlev][demov][endv]concat=n=3:v=1:a=0[v]")
$f.Add("[5:a]asetpts=PTS-STARTPTS,afade=in:st=0:d=0.5,afade=out:st=$($total - 0.6):d=0.6[a]")
$graph = $f -join ";`n"
Set-Content "$build\graph.txt" $graph -Encoding ASCII

$out = Join-Path $dir 'Windshot-trailer.mp4'
& $ffmpeg -hide_banner -loglevel error -y @inputs -/filter_complex "$build\graph.txt" -map '[v]' -map '[a]' `
    -c:v libx264 -preset slow -crf 17 -pix_fmt yuv420p -r 30 -c:a aac -b:a 256k -t $total -movflags +faststart $out
if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed ($LASTEXITCODE)." }
'Wrote {0} ({1:N1} MB) and the thumbnail.' -f $out, ((Get-Item $out).Length / 1MB)
