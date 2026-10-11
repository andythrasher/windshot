# Cuts the recording from record-zoom.ps1 into the zoom tutorial, styled like the Store trailer:
# the brand gradient, a caption per step, and the screen recording inset with rounded corners and
# a shadow, framed on what matters in each step. A title card first, then the steps, the finished
# video, and an end card. No sound.
#
#   powershell -File tools/tutorial/assemble-zoom.ps1 [-FfmpegPath <path to ffmpeg.exe>]
#
# Output in artifacts\tutorial: Windshot-zoom-tutorial.mp4 (1920x1080, 30 fps, H.264).
param([string]$FfmpegPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$dir = Join-Path $root 'artifacts\tutorial'
$build = Join-Path $dir 'build'
New-Item -ItemType Directory -Force $build | Out-Null
. "$PSScriptRoot\..\trailer\find-ffmpeg.ps1"
$ffmpeg = Find-Ffmpeg $FfmpegPath
$recording = Join-Path $dir 'recording.mkv'
$marks = Get-Content (Join-Path $dir 'marks.json') -Raw | ConvertFrom-Json
$result = Get-ChildItem (Join-Path $dir 'Videos') -Filter *.mp4 | Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $result) { throw 'No saved video in artifacts\tutorial\Videos.' }
$inv = [Globalization.CultureInfo]::InvariantCulture
function N([double]$v) { $v.ToString('0.###', $inv) }

# The inset, and the caption band above it.
$insetW = 1728; $insetH = 972; $insetX = 96; $insetY = 96

# A 16:9 region of the screen around a rectangle, with some margin, kept on the screen.
# Within the work area, in physical pixels, so the taskbar never shows.
Add-Type -AssemblyName System.Windows.Forms
Add-Type 'using System; using System.Runtime.InteropServices; public static class TutorialDpi { [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v); }'
[TutorialDpi]::SetProcessDpiAwarenessContext([IntPtr]-4) | Out-Null
$workArea = [Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$screenW = $workArea.Right; $screenH = $workArea.Bottom
function Around($r, [double]$margin = 60) {
    $w = $r.Width + 2 * $margin; $h = $r.Height + 2 * $margin
    if ($w / $h -lt 16 / 9) { $w = $h * 16 / 9 } else { $h = $w * 9 / 16 }
    $w = [Math]::Min($w, $screenW); $h = [Math]::Min($h, $screenH)
    $x = [Math]::Min([Math]::Max(0, $r.X + $r.Width / 2 - $w / 2), $screenW - $w)
    $y = [Math]::Min([Math]::Max(0, $r.Y + $r.Height / 2 - $h / 2), $screenH - $h)
    # Even numbers, for the encoder.
    'crop={0}:{1}:{2}:{3}' -f ([int]($w / 2) * 2), ([int]($h / 2) * 2), ([int]($x / 2) * 2), ([int]($y / 2) * 2)
}
$board = $marks.boardRect; $editor = $marks.editorRect
$onBoard = Around $board
$onEditor = Around $editor 30
# Part of the editor, as fractions of its window: left, top, right, bottom.
function Part([double]$l, [double]$t, [double]$r, [double]$b) {
    Around ([pscustomobject]@{ X = $editor.X + $editor.Width * $l; Y = $editor.Y + $editor.Height * $t; Width = $editor.Width * ($r - $l); Height = $editor.Height * ($b - $t) }) 20
}
$timeline = Part 0 0.55 1 1          # the canvas's lower half and the timeline
$drawing = Part 0 0 1 0.8            # the toolbar and the canvas
$menu = Part 0 0 0.55 0.5            # the toolbar's left end, the options bar and its menu

# The steps: what part of the recording each one shows, framed where, and its caption.
$scenes = @(
    @{ Start = $marks.record - 0.3; End = $marks.stop; Crop = $onBoard; Caption = '1.  Press Ctrl+Shift+5, select an area, and record' },
    @{ Start = $marks.stop; End = $marks.scrub; Crop = $onEditor; Caption = '2.  Press Ctrl+Shift+5 again to stop. The recording opens in the editor' },
    @{ Start = $marks.scrub; End = $marks.zoomtool; Crop = $timeline; Caption = '3.  Move the playhead to where the zoom should start' },
    @{ Start = $marks.zoomtool; End = $marks.speed; Crop = $drawing; Caption = '4.  Pick the Zoom tool (Z) and drag over the area to zoom in to' },
    @{ Start = $marks.speed; End = $marks.length; Crop = $menu; Caption = '5.  The Zoom menu sets how fast it zooms in and back out' },
    @{ Start = $marks.length; End = $marks.play; Crop = $timeline; Caption = '6.  Drag the ends of its bar to set when it starts and ends' },
    @{ Start = $marks.play; End = $marks.save; Crop = $drawing; Caption = '7.  Press Space to watch it' },
    @{ Start = $marks.save; End = $marks.end; Crop = $drawing; Caption = '8.  Save it as an MP4 or a GIF' }
)
$titleLength = 3; $endLength = 3.5
$resultLength = [double](& (Join-Path (Split-Path $ffmpeg) 'ffprobe.exe') -v error -show_entries format=duration -of csv=p=0 $result.FullName)

# ---- Pictures: background, mask, captions, title and end cards ---------------------------
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

$bmp, $g = Gradient 1920 1080
Shadow $g $insetX $insetY $insetW $insetH 14
$g.Dispose(); $bmp.Save("$build\background.png"); $bmp.Dispose()

$bmp = New-Object Drawing.Bitmap $insetW, $insetH
$g = [Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'; $g.Clear([Drawing.Color]::Black)
$g.FillPath([Drawing.Brushes]::White, (Rounded 0 0 $insetW $insetH 14))
$g.Dispose(); $bmp.Save("$build\mask.png"); $bmp.Dispose()

$captions = @($scenes | ForEach-Object Caption) + 'The result: the video zooms in on the task, then back out'
for ($i = 0; $i -lt $captions.Count; $i++) {
    $bmp = New-Object Drawing.Bitmap 1920, 96
    $g = [Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    Centered $g $captions[$i] (Font 40) 6 88
    $g.Dispose(); $bmp.Save("$build\caption$i.png"); $bmp.Dispose()
}

$icon = [Drawing.Image]::FromFile((Join-Path $root 'src\Windshot\Assets\Package\Square150x150Logo.scale-400.png'))
$bmp, $g = Gradient 1920 1080
$g.DrawImage($icon, 810, 190, 300, 300)
Centered $g 'Zoom in on what matters' (Font 96) 520 140
Centered $g 'Recording with Windshot: the Zoom tool' (Font 44 'Regular') 660 80
$g.Dispose(); $bmp.Save("$build\title.png"); $bmp.Dispose()

$bmp, $g = Gradient 1920 1080
$g.DrawImage($icon, 810, 150, 300, 300)
Centered $g 'Windshot' (Font 120) 450 150
Centered $g 'Free on the Microsoft Store' (Font 54) 610 90
Centered $g 'windshot.app' (Font 40 'Regular') 700 70 (New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(220, 255, 255, 255)))
$g.Dispose(); $bmp.Save("$build\end.png"); $bmp.Dispose()
$icon.Dispose()

# ---- The cut ---------------------------------------------------------------------------
$n = $scenes.Count
$inputs = @('-i', $recording, '-i', $result.FullName,
            '-loop', '1', '-framerate', '30', '-i', "$build\background.png",
            '-loop', '1', '-framerate', '30', '-i', "$build\mask.png",
            '-loop', '1', '-framerate', '30', '-t', $titleLength, '-i', "$build\title.png",
            '-loop', '1', '-framerate', '30', '-t', $endLength, '-i', "$build\end.png")
$firstCaption = 6
foreach ($i in 0..$n) { $inputs += @('-loop', '1', '-framerate', '30', '-i', "$build\caption$i.png") }

$f = New-Object Collections.Generic.List[string]
# Windshot keeps its recording outline out of screen captures; draw it back in while recording.
$box = 'drawbox=x={0}:y={1}:w={2}:h={3}:color=0xE81123:t=6:enable=''between(t,{4},{5})''' -f ($board.X - 3), ($board.Y - 3), ($board.Width + 6), ($board.Height + 6), (N $marks.recording), (N $marks.stop)
$f.Add("[0:v]$box,split=$n" + (($(0..($n - 1)) | ForEach-Object { "[s$_]" }) -join ''))
$names = @(); $durations = @()
for ($i = 0; $i -lt $n; $i++) {
    $sc = $scenes[$i]
    $length = $sc.End - $sc.Start; $durations += $length
    $f.Add(("[s{0}]trim=start={1}:end={2},setpts=PTS-STARTPTS,{3},scale={4}:{5}:flags=lanczos,fps=30,setsar=1,format=yuva420p[c{0}]" -f $i, (N $sc.Start), (N $sc.End), $sc.Crop, $insetW, $insetH))
    $names += "[c$i]"
}
# The finished video, cropped to 16:9 around its middle.
$f.Add(("[1:v]crop=iw:iw*9/16:0:(ih-iw*9/16)/2,scale={0}:{1}:flags=lanczos,fps=30,setsar=1,format=yuva420p[c{2}]" -f $insetW, $insetH, $n))
$names += "[c$n]"; $durations += $resultLength
$f.Add(("{0}concat=n={1}:v=1:a=0[demo]" -f ($names -join ''), ($n + 1)))
$demoLength = ($durations | Measure-Object -Sum).Sum
$f.Add("[3:v]format=gray,trim=duration=$(N $demoLength)[mask]")
$f.Add("[demo][mask]alphamerge[rounded]")
$f.Add("[2:v]trim=duration=$(N $demoLength),format=yuva420p[bg]")
$f.Add("[bg][rounded]overlay=$($insetX):$($insetY)[framed0]")
$last = 'framed0'; $at = 0
for ($i = 0; $i -le $n; $i++) {
    $d = $durations[$i]
    $f.Add(("[{0}:v]format=rgba,trim=duration={1},fade=in:st=0:d=0.25:alpha=1,fade=out:st={2}:d=0.2:alpha=1,setpts=PTS+{3}/TB[h{4}]" -f ($firstCaption + $i), (N $d), (N ($d - 0.2)), (N $at), $i))
    $f.Add(("[{0}][h{1}]overlay=0:0:eof_action=pass[framed{2}]" -f $last, $i, ($i + 1)))
    $last = "framed$($i + 1)"; $at += $d
}
$f.Add("[$last]format=yuv420p,setsar=1[demov]")
$f.Add("[4:v]format=yuv420p,setsar=1,fade=in:st=0:d=0.4[titlev]")
$f.Add("[5:v]format=yuv420p,setsar=1,fade=out:st=$(N ($endLength - 0.4)):d=0.4[endv]")
$f.Add("[titlev][demov][endv]concat=n=3:v=1:a=0[v]")
Set-Content "$build\graph.txt" ($f -join ";`n") -Encoding ASCII

$out = Join-Path $dir 'Windshot-zoom-tutorial.mp4'
$total = $titleLength + $demoLength + $endLength
& $ffmpeg -hide_banner -loglevel error -y @inputs -/filter_complex "$build\graph.txt" -map '[v]' `
    -c:v libx264 -preset slow -crf 18 -pix_fmt yuv420p -r 30 -t (N $total) -movflags +faststart $out
if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed ($LASTEXITCODE)." }
'Wrote {0} ({1:N1} MB, {2:N1} s).' -f $out, ((Get-Item $out).Length / 1MB), $total
