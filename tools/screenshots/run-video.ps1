# Makes the video screenshots: 11-video (a recording in the editor, with an arrow, a note and a
# highlight, a layer's bar on the timeline and the Animate menu open) and 12-zoom (the view zoomed in
# on the task table). Drives the dev build (dotnet build -c Debug first) over the demo board, with
# hands off the mouse and keyboard for about 40 seconds; its settings are put back afterwards.
#
#   powershell -File tools/screenshots/run-video.ps1
param()
$ErrorActionPreference = 'Stop'
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\screenshots\lib-shots.ps1"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script:exe = Join-Path $root 'src\Windshot\bin\Debug\net10.0-windows10.0.26100.0\win-x64\Windshot.exe'
if (-not (Test-Path $exe)) { throw "No Debug build at $exe; run dotnet build -c Debug first." }
$outDir = Join-Path $root 'artifacts\screenshots-video'
New-Item -ItemType Directory -Force $outDir, "$outDir\Videos" | Out-Null
$settingsFile = "$env:LOCALAPPDATA\Windshot\settings.json"

# ---- Like a hand: eased glides --------------------------------------------------------
function Ease([double]$t) { $t * $t * (3 - 2 * $t) }
function Here { $p = [Windows.Forms.Cursor]::Position; @($p.X, $p.Y) }
function Glide($to, [int]$ms = 450) {
  $from = Here; $steps = [Math]::Max(2, [int]($ms / 15))
  for ($i = 1; $i -le $steps; $i++) {
    $e = Ease ($i / $steps)
    MoveAbs ([int]($from[0] + ($to[0] - $from[0]) * $e)) ([int]($from[1] + ($to[1] - $from[1]) * $e))
    Start-Sleep -Milliseconds 15
  }
}
function Press { [Wt]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Start-Sleep -m 60 }
function Release { [Wt]::mouse_event(4,0,0,0,[UIntPtr]::Zero); Start-Sleep -m 60 }
function GlideClick($at, [int]$ms = 450) { Glide $at $ms; Pump 120; Press; Release; Pump 150 }
function GlideDrag($a, $b, [int]$ms = 700) { Glide $a 450; Pump 120; Press; Glide $b $ms; Pump 80; Release; Pump 200 }
function Hold([int]$ms) { Pump $ms }
function At($el, [double]$fx = 0.5, [double]$fy = 0.5) { $b = $el.Current.BoundingRectangle; @([int]($b.X + $b.Width * $fx), [int]($b.Y + $b.Height * $fy)) }

# ---- Scene marks ---------------------------------------------------------------------
$clock = [Diagnostics.Stopwatch]::new()
$marks = [ordered]@{}
function Mark([string]$name) { $marks[$name] = [Math]::Round($clock.Elapsed.TotalSeconds, 3); "  $name at $($marks[$name])s" }

# ---- The editor: only ever the dev build's, visible and in front ---------------------
function Editor {
  $h = [Wt]::FindWindow([NullString]::Value, 'Windshot recording')
  if ($h -eq [IntPtr]::Zero -or -not [Wt]::IsWindowVisible($h) -or [Wt]::GetForegroundWindow() -ne $h) { throw 'The recording editor is not visible in front; aborting.' }
  $procId = 0; [void][Win32Pid]::GetWindowThreadProcessId($h, [ref]$procId)
  if ((Get-Process -Id $procId).Path -ne $exe) { throw 'That editor is not the dev build; aborting.' }
  if (Get-Content $log | Select-Object -Skip $script:logStart | Select-String 'Unhandled|failed') { throw 'Windshot logged an error; aborting.' }
  return $h
}
Add-Type 'using System; using System.Runtime.InteropServices; public static class Win32Pid { [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out int pid); }'
function Inside($p) {
  $f = FrameBounds $script:editor
  if (-not $f.Contains($p[0], $p[1])) { throw "Point $p is outside the editor; aborting." }
  return $p
}

# ---- Settings for the recording -------------------------------------------------------
$videos = ("$outDir\Videos").Replace('\', '\\')
$recordSettings = @"
{ "ShowWelcome": false,
  "Appearance": { "Window": "Windshot", "Colors": "Windshot" },
  "Editor": { "ShowLayers": false, "CloseAfterSaveOrCopy": false, "TextBackground": true, "Shape": "Rectangle", "HighlighterBlend": "Multiply" },
  "Saving": { "SaveWithoutAsking": true },
  "Video": { "FrameRate": 30, "Cursor": true, "Sound": false, "Format": "MP4", "Folder": "$videos" } }
"@
function Restart-Windshot([switch]$Dev) {
  if (@(Get-Process Windshot -ErrorAction SilentlyContinue | Where-Object MainWindowTitle -ne '').Count) { throw 'A Windshot window is open; close it first.' }
  Get-Process Windshot -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 800
  if ($Dev) {
    Start-Process $exe --no-welcome
    for ($i = 0; $i -lt 40 -and -not ((Get-Content $log -Tail 1) -match 'Started'); $i++) { Start-Sleep -Milliseconds 250 }
    Start-Sleep -Milliseconds 2000
  }
}

$backup = "$settingsFile.screenshot-backup"
if (-not (Test-Path $backup)) { Copy-Item $settingsFile $backup }
$recorder = $null
$stage = $null
try {
  Set-Content $settingsFile $recordSettings -Encoding UTF8
  Restart-Windshot -Dev
  AssertFreshStart
  $stage = Show-Stage
  $b = $boardRect
  function P([int]$x, [int]$y) { @(($b.X + $x), ($b.Y + $y)) }   # a point on the board, on screen
  MoveAbs ($b.Right - 300) ($b.Bottom + 60); Pump 400

  $clock.Start()
  Hold 1500

  # ---- 1. Record: the shortcut, an area, then a little activity to zoom in on.
  Mark 'record'
  Key @(0x11, 0x10, 0x35); Hold 900
  if (-not ((Get-Content $log -Tail 1) -match 'Overlays shown')) { throw 'The selection overlay did not appear; aborting.' }
  Glide (P 2 2) 700; Hold 200
  Press; Glide (P $b.Width $b.Height) 1300; Hold 100; Release
  $recordingFrom = $clock.Elapsed.TotalSeconds
  Mark 'recording'
  Hold 700
  if (-not (Get-Content $log -Tail 2 | Select-String 'Recording \{')) { throw 'Recording did not start; aborting.' }
  Glide (P 900 640) 900; Hold 600                  # over the chart
  Glide (P 1410 1150) 1000                         # to the blocked task
  $marks['arrived'] = [Math]::Round($clock.Elapsed.TotalSeconds - $recordingFrom, 3)   # in the recording's time
  Hold 2600
  Glide (P 1860 105) 900; Hold 900                 # and on to Share
  Mark 'stop'
  Key @(0x11, 0x10, 0x35)
  # The editor takes a few seconds to open a recording.
  for ($i = 0; $i -lt 48; $i++) {
    Hold 250
    $h = [Wt]::FindWindow([NullString]::Value, 'Windshot recording')
    if ($h -eq [IntPtr]::Zero -or -not [Wt]::IsWindowVisible($h)) { continue }
    if ([Wt]::GetForegroundWindow() -eq $h) { break }
    # Open but behind the stage: bring it forward, if it's the dev build's.
    $procId = 0; [void][Win32Pid]::GetWindowThreadProcessId($h, [ref]$procId)
    if ((Get-Process -Id $procId).Path -eq $exe) { [void][Wt]::SetForegroundWindow($h) }
  }
  $h = [Wt]::FindWindow([NullString]::Value, 'Windshot recording')
  "  editor window: found=$($h -ne [IntPtr]::Zero) visible=$([Wt]::IsWindowVisible($h)) front=$([Wt]::GetForegroundWindow() -eq $h)"
  Hold 1200

  # ---- 2. The editor.
  $script:editor = Editor
  Mark 'editor'
  $marks['editorRect'] = (FrameBounds $editor | Select-Object X, Y, Width, Height)
  $win = $A::FromHandle($editor)
  function Find($id) { $el = $win.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition $A::AutomationIdProperty, $id)); if (-not $el) { throw "$id not found; aborting." }; $el }
  function FindAnywhere($id) {
    $el = $A::RootElement.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition $A::AutomationIdProperty, $id))
    if (-not $el) { throw "$id not found; aborting." }; $el
  }
  $time = (Find 'TimeLabel').Current
  $length = [TimeSpan]::ParseExact(($time.Name -split ' / ')[1], 'm\:ss\.f', $null).TotalSeconds
  $play = (Find 'PlayButton').Current.BoundingRectangle
  $timeRect = $time.BoundingRectangle
  $scale = 1.5
  # The timeline canvas sits right of the time, 80 DIP tall, centered on the play button.
  $canvasLeft = $timeRect.Right + 10 * $scale
  $canvasTop = ($play.Y + $play.Height / 2) - 40 * $scale
  $frame = FrameBounds $editor
  $stripLeft = $canvasLeft + 10 * $scale
  $stripWidth = ($frame.Right - 16 * $scale - 8) - $canvasLeft - 20 * $scale
  function StripAt([double]$s) { @([int]($stripLeft + $stripWidth * $s / $length), [int]($canvasTop + 30 * $scale)) }
  function LaneAt([double]$s) { @([int]($stripLeft + $stripWidth * $s / $length), [int]($canvasTop + 67 * $scale)) }

  # Where the board is in the editor: find its dark sidebar on screen, and check it.
  $zoom = [double]((Find 'ZoomButton').Current.Name -replace '[^\d]', '') / 100
  $bar = (Find 'OptionsBar').Current.BoundingRectangle
  $grab = New-Object Drawing.Bitmap $frame.Width, $frame.Height
  [Drawing.Graphics]::FromImage($grab).CopyFromScreen($frame.X, $frame.Y, 0, 0, $grab.Size)
  $navy = ([Drawing.Bitmap][Drawing.Image]::FromFile("$shots\demo-board.png")).GetPixel(40, 700)
  function IsNavy($c) { [Math]::Abs($c.R - $navy.R) -le 10 -and [Math]::Abs($c.G - $navy.G) -le 10 -and [Math]::Abs($c.B - $navy.B) -le 10 }
  $midY = [int](($bar.Bottom + $play.Top) / 2) - $frame.Y
  $left = -1; for ($x = 0; $x -lt $frame.Width; $x++) { if (IsNavy $grab.GetPixel($x, $midY)) { $left = $x; break } }
  if ($left -lt 0) { throw 'Could not find the board in the editor; aborting.' }
  $top = -1; for ($y = [int]($bar.Bottom - $frame.Y); $y -lt $frame.Height; $y++) { if (IsNavy $grab.GetPixel($left + 30, $y)) { $top = $y; break } }
  if ($top -lt 0) { throw 'Could not find the top of the board in the editor; aborting.' }
  $ix = $frame.X + $left; $iy = $frame.Y + $top
  function B([int]$x, [int]$y) { Inside @([int]($ix + $x * $zoom), [int]($iy + $y * $zoom)) }
  $share = B 1860 105; $c = $grab.GetPixel($share[0] - $frame.X, $share[1] - $frame.Y)
  if (-not ($c.B -gt 150 -and $c.R -lt 120)) { throw "The board's Share button is not where expected ($c); aborting." }
  Hold 1200

  # ---- A second in: a highlight on the task, a note above it, and an arrow from the note to its status.
  GlideDrag (Inside (StripAt 0.2)) (Inside (StripAt 1.0)) 500; Hold 400
  Key @(0x48)                                            # H
  GlideDrag (B 405 1150) (B 640 1150) 600
  Key @(0x54)                                            # T
  GlideClick (B 1150 905) 400; Hold 300
  foreach ($ch in 'Stuck since Tuesday'.ToCharArray()) { [Windows.Forms.SendKeys]::SendWait([string]$ch); Start-Sleep -m 40 }
  Hold 300; Key @(0x1B); Hold 300
  Key @(0x41)                                            # A
  GlideDrag (B 1380 985) (B 1440 1132) 600
  # A zoom on the table, later on, so the first shot is still zoomed out.
  Key @(0x1B); Key @(0x56)
  GlideDrag (Inside (StripAt 1.2)) (Inside (StripAt 2.6)) 500; Hold 400
  Key @(0x5A)                                            # Z
  GlideDrag (B 360 900) (B 1520 1262) 700
  Hold 500

  # ---- 11-video: two seconds in (the highlight has drawn on), the arrow selected, coming in with a pop.
  Key @(0x1B); Key @(0x56)                               # Esc, Select
  GlideDrag (Inside (StripAt 2.7)) (Inside (StripAt 2.0)) 500; Hold 500
  GlideClick (B 1410 1058) 500; Hold 500
  GlideClick (At (Find 'AnimateButton')) 500; Hold 700
  $in = FindAnywhere 'InStylePicker'
  $in.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Hold 400
  $pop = $in.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition $A::NameProperty, 'Pop'))
  if (-not $pop) { throw 'Pop not found; aborting.' }
  $pop.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Hold 300
  try { $in.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch {}
  Glide (B 1900 560) 400; Hold 800
  $shot = Grab (FrameBounds $editor); $shot.Save("$shots\raw-video.png"); Compose $shot 'Record video, then annotate it.' '11-video'

  # ---- 12-zoom: nothing selected, the playhead in the middle of the zoom.
  Key @(0x1B); Hold 300; Key @(0x1B); Hold 300
  GlideDrag (Inside (StripAt 2.1)) (Inside (StripAt 4.6)) 600; Hold 1200
  Glide (B 1900 200) 300; Hold 600
  $shot = Grab (FrameBounds $editor); $shot.Save("$shots\raw-zoom.png"); Compose $shot 'Zoom in on what matters.' '12-zoom'
} finally {
  Get-Process Windshot -ErrorAction SilentlyContinue | Where-Object Path -eq $exe | Stop-Process -Force
  if ($stage) { $stage.Close() }
  Start-Sleep -Milliseconds 800
  Copy-Item $backup $settingsFile -Force; Remove-Item $backup
  Get-ChildItem "$env:LOCALAPPDATA\Windshot\Recordings" -Filter *.mp4 -ErrorAction SilentlyContinue | Remove-Item -ErrorAction SilentlyContinue
  if (Get-AppxPackage Windshot.Dev) { Start-Process explorer.exe 'shell:AppsFolder\Windshot.Dev_8c34qc8fe7vjr!App' }
  'Settings are back.'
}
