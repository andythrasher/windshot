. "$PSScriptRoot\lib-shots.ps1"
AssertFreshStart
$stage = Show-Stage
function BS($bx, $by) { @(($boardRect.X + $bx), ($boardRect.Y + $by)) }
try {
  # 1. The capture overlay mid-selection, with the magnifier: drag and hold, grab, then cancel.
  Start-Process $exe; Pump 1500
  if (-not ((Get-Content $log -Tail 1) -match 'Overlays shown')) { throw 'Capture overlay did not appear; aborting.' }
  $a = BS 1446 372; $b = BS 1958 818
  MoveAbs $a[0] $a[1]; Pump 100; [Wt]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
  for ($i = 1; $i -le 10; $i++) { MoveAbs ($a[0] + ($b[0]-$a[0])*$i/10) ($a[1] + ($b[1]-$a[1])*$i/10); Pump 40 }
  Pump 500
  $g = Grab $work; $g.Save("$shots\raw-overlay.png")
  Key @(0x1B); Pump 300
  [Wt]::mouse_event(4,0,0,0,[UIntPtr]::Zero); Pump 500
  if ((Get-Process Windshot | Where-Object MainWindowTitle -eq 'Windshot')) { throw 'An editor opened unexpectedly; aborting.' }
  Compose $g 'Capture any region or window.' '02-capture' 0

  # 2. Pin the weekly progress card and drag it aside, floating over the app.
  Start-Process $exe; Pump 1500
  if (-not ((Get-Content $log -Tail 1) -match 'Overlays shown')) { throw 'Capture overlay did not appear; aborting.' }
  Drag (BS 393 378) (BS 1415 815); Pump 2000
  if ($null -eq (WindowRect 'Windshot')) { throw 'Editor not visible in front; aborting.' }
  Key @(0x11, 0x50); Pump 1500
  $from = BS 900 600; $to = BS 1500 1000
  MoveAbs $from[0] $from[1]; Pump 100; [Wt]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
  for ($i = 1; $i -le 12; $i++) { MoveAbs ($from[0] + ($to[0]-$from[0])*$i/12) ($from[1] + ($to[1]-$from[1])*$i/12); Pump 30 }
  [Wt]::mouse_event(4,0,0,0,[UIntPtr]::Zero); Pump 300
  MoveAbs ($work.Right - 40) ($work.Bottom - 40); Pump 500
  $g = Grab $work; $g.Save("$shots\raw-pin.png")
  Compose $g 'Pin it above everything.' '06-pin' 0
  # Middle-click closes the pin (and deletes its files).
  MoveAbs $to[0] $to[1]; Pump 200; [Wt]::mouse_event(0x20,0,0,0,[UIntPtr]::Zero); [Wt]::mouse_event(0x40,0,0,0,[UIntPtr]::Zero); Pump 600
} finally { $stage.Close() }
"pins left: $(@(Get-ChildItem "$env:LOCALAPPDATA\Windshot\pins" -File -ErrorAction SilentlyContinue).Count); editors open: $(@(Get-Process Windshot | Where-Object MainWindowTitle -eq 'Windshot').Count)"
Get-Content $log | Select-Object -Skip $script:logStart | Select-String 'Unhandled|failed|Couldn'
'done'
