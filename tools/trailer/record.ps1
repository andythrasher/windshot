# Records the trailer's demo: the whole screen, while this drives Windshot through each scene
# with eased pointer movement, like a person would. Scene start times go to marks.json, for
# tools/trailer/assemble.ps1 to cut to the music. Hands off the mouse and keyboard (about 1.5 min).
#
#   powershell -File tools/trailer/record.ps1
#
# Like tools/screenshots/run-all.ps1, it puts known settings in place, restarts Windshot, and
# puts your settings (and clipboard text) back afterwards. Saves go to artifacts\trailer\Screenshots,
# and the editor stays open after saving, so the finished image is on screen at the end.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\screenshots\lib-shots.ps1"
$ffmpeg = Get-ChildItem 'C:\dev\tools\ffmpeg' -Recurse -Filter ffmpeg.exe | Select-Object -First 1 -ExpandProperty FullName
if (-not $ffmpeg) { throw 'ffmpeg not found under C:\dev\tools\ffmpeg.' }
$outDir = Join-Path $PSScriptRoot '..\..\artifacts\trailer'
New-Item -ItemType Directory -Force $outDir, "$outDir\Screenshots" | Out-Null
$outDir = (Resolve-Path $outDir).Path
$settingsFile = "$env:LOCALAPPDATA\Windshot\settings.json"
$log = "$env:LOCALAPPDATA\Windshot\windshot.log"

# ---- Like a hand: eased glides, visible typing ------------------------------------------
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
function TypeSlowly([string]$text) { foreach ($ch in $text.ToCharArray()) { [Windows.Forms.SendKeys]::SendWait(($ch -replace '([+^%~(){}\[\]!])', '{$1}')); Start-Sleep -m 70 } }
function Hold([int]$ms) { Pump $ms }
function FindAnywhere($id, [switch]$ByName) {
  $windshot = @(Get-Process Windshot).Id
  $prop = if ($ByName) { $A::NameProperty } else { $A::AutomationIdProperty }
  foreach ($w in $A::RootElement.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)) {
    if ($windshot -notcontains $w.Current.ProcessId) { continue }
    $el = $w.FindFirst([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new($prop, $id))
    if ($el) { return $el }
  }
  throw "$id not found; aborting."
}
function At($el, [double]$fx = 0.5, [double]$fy = 0.5) { $b = $el.Current.BoundingRectangle; @([int]($b.X + $b.Width * $fx), [int]($b.Y + $b.Height * $fy)) }

# ---- Scene marks ---------------------------------------------------------------------
$clock = [Diagnostics.Stopwatch]::new()
$marks = [ordered]@{}
function Mark([string]$name) { $marks[$name] = [Math]::Round($clock.Elapsed.TotalSeconds, 3); "  $name at $($marks[$name])s" }

# ---- Settings for the recording -------------------------------------------------------
$folder = ("$outDir\Screenshots").Replace('\', '\\')
$recordSettings = @"
{ "Appearance": { "Window": "Windshot", "Colors": "Windshot" },
  "Editor": { "Shape": "Rectangle", "FillShapes": false, "HighlighterBlend": "Multiply", "ShowLayers": false, "CloseAfterSaveOrCopy": false },
  "Beautify": { "Preset": "Sunset", "Padding": 72 },
  "Saving": { "SaveWithoutAsking": true, "Folder": "$folder" } }
"@
function Restart-Windshot {
  if (@(Get-Process Windshot -ErrorAction SilentlyContinue | Where-Object MainWindowTitle -ne '').Count) { throw 'A Windshot window is open; close it first.' }
  Get-Process Windshot -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 800
  Start-Process $exe
  for ($i = 0; $i -lt 40 -and -not ((Get-Content $log -Tail 1) -match 'Started'); $i++) { Start-Sleep -Milliseconds 250 }
  Start-Sleep -Milliseconds 2000
}

$backup = "$settingsFile.trailer-backup"
if (-not (Test-Path $backup)) { Copy-Item $settingsFile $backup }
$savedText = if ([Windows.Forms.Clipboard]::ContainsText()) { [Windows.Forms.Clipboard]::GetText() } else { $null }
$recorder = $null
try {
  Set-Content $settingsFile $recordSettings -Encoding UTF8
  Restart-Windshot
  AssertFreshStart
  $stage = Show-Stage
  MoveAbs ($boardRect.Right - 300) ($boardRect.Bottom + 60); Pump 400

  # The phone goes on the clipboard now, so pasting it later is instant.
  $png = "$shots\demo-phone.png"
  $data = New-Object Windows.Forms.DataObject
  $data.SetData('PNG', $false, (New-Object IO.MemoryStream (,[IO.File]::ReadAllBytes($png))))
  $data.SetImage([Drawing.Image]::FromFile($png))
  [Windows.Forms.Clipboard]::SetDataObject($data, $true)

  # Start recording: Desktop Duplication into NVENC, near-lossless.
  $info = New-Object Diagnostics.ProcessStartInfo $ffmpeg, "-hide_banner -loglevel error -y -f lavfi -i ddagrab=output_idx=0:framerate=30:draw_mouse=1 -c:v h264_nvenc -preset p5 -rc constqp -qp 14 `"$outDir\recording.mkv`""
  $info.UseShellExecute = $false; $info.RedirectStandardInput = $true; $info.CreateNoWindow = $true
  $recorder = [Diagnostics.Process]::Start($info)
  $clock.Start()
  Hold 1500

  # ---- Capture: the hotkey, then a region over the app.
  Mark 'capture'
  Key @(0x11, 0x10, 0x32); Hold 900
  Glide @(($boardRect.X + 2), ($boardRect.Y + 2)) 700; Hold 200
  Press; Glide @($boardRect.Right, $boardRect.Bottom) 1400; Hold 100; Release
  Hold 1600
  if ($null -eq (WindowRect 'Windshot')) { throw 'Editor not visible in front; aborting.' }
  $script:editor = EditorHandle
  $frame = FrameBounds $script:editor; $bar = (FindIn $script:editor 'OptionsBar').Current.BoundingRectangle
  $script:ix = [int]($frame.X + ($frame.Width - $boardRect.Width) / 2)
  $script:iy = [int]($bar.Bottom + ($frame.Bottom - $bar.Bottom - $boardRect.Height) / 2)

  # ---- Annotate: an arrow and a note on the best day, a highlight, steps, a blurred key.
  Mark 'annotate'
  Key @(0x41); GlideDrag (B 1290 520) (B 1080 600) 650
  Key @(0x54); GlideClick (B 1150 455) 400; Hold 200
  TypeSlowly 'Best week yet!'; Hold 250; Key @(0x1B); Hold 200
  Key @(0x48); GlideDrag (B 418 1008) (B 612 1008) 600
  Key @(0x4E); foreach ($y in 936, 1008, 1080) { GlideClick (B 1560 $y) 350 }
  Mark 'blur'
  Key @(0x42); GlideDrag (B 1488 584) (B 1916 634) 800
  Hold 500

  # ---- A picture: paste the phone, move it over the sidebar, tilt it, outline and shadow.
  Mark 'picture'
  Key @(0x56); Key @(0x1B); Key @(0x11, 0x56); Hold 700
  GlideDrag (B 990 645) (B 230 800) 900
  GlideDrag (B 230 428) (B 191 430) 600
  $style = FindIn $script:editor 'StyleButton'; GlideClick (At $style) 500; Hold 500
  $outline = FindAnywhere 'OutlineSlider'; $shadow = FindAnywhere 'ShadowSlider'
  $track = $outline.Current.BoundingRectangle
  GlideDrag (At $outline 0.03 0.72) @([int]($track.X + $track.Width * 0.32), [int]($track.Y + $track.Height * 0.72)) 500
  $track = $shadow.Current.BoundingRectangle
  GlideDrag (At $shadow 0.03 0.72) @([int]($track.X + $track.Width * 0.72), [int]($track.Y + $track.Height * 0.72)) 600
  Hold 700; Key @(0x1B); Hold 300

  # ---- Layers: open the panel, Shift-click the three steps, recolor them together.
  Mark 'layers'
  $layers = FindIn $script:editor 'LayersButton'; GlideClick (At $layers) 500; Hold 700
  GlideClick (At (FindAnywhere 'Step 1' -ByName)) 450
  [Wt]::keybd_event(0x10,0,0,[UIntPtr]::Zero); GlideClick (At (FindAnywhere 'Step 3' -ByName)) 450; [Wt]::keybd_event(0x10,0,2,[UIntPtr]::Zero)
  Hold 300; Key @(0x35); Hold 700
  Key @(0x1B); Hold 300                                       # deselect, so the reveal is clean
  GlideClick (At $layers) 450; Hold 600

  # ---- Beautify, then admire it.
  Mark 'beautify'
  $beautify = FindIn $script:editor 'BeautifyButton'; $b = $beautify.Current.BoundingRectangle
  GlideClick @([int]($b.X + $b.Height / 2), [int]($b.Y + $b.Height / 2)) 600
  Glide @(($frame.Right - 120), ($frame.Bottom - 80)) 900
  Hold 2500

  # ---- Save: straight to the folder; the editor stays open, so the result shows under the message.
  Mark 'save'
  $save = FindIn $script:editor 'SaveButton'; GlideClick (At $save) 700
  Hold 2200
  Mark 'end'
  Hold 500
} finally {
  if ($recorder -and -not $recorder.HasExited) {
    $clock.Stop()
    $recorder.StandardInput.Write('q'); $recorder.StandardInput.Flush()
    $recorder.WaitForExit(15000) | Out-Null
  }
  $marks['stopped'] = [Math]::Round($clock.Elapsed.TotalSeconds, 3)
  $marks | ConvertTo-Json | Set-Content "$outDir\marks.json" -Encoding UTF8
  try { if ([Wt]::FindWindow([NullString]::Value, 'Windshot') -ne [IntPtr]::Zero) { CloseEditor } } catch {}
  if ($stage) { $stage.Close() }
  if ($null -ne $savedText) { [Windows.Forms.Clipboard]::SetText($savedText) } else { [Windows.Forms.Clipboard]::Clear() }
  Start-Sleep -Milliseconds 800
  Copy-Item $backup $settingsFile -Force; Remove-Item $backup
  Restart-Windshot
  'Your settings are back.'
}
Get-Content $log | Select-Object -Skip $script:logStart | Select-String 'Saved|Inserted|Unhandled|failed|Couldn'
