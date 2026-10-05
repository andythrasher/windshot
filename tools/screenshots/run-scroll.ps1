. "$PSScriptRoot\lib-shots.ps1"
AssertFreshStart
$stage = Show-LongStage
try {
  # Scrolling capture of the release notes: pick the page (not its scrollbar), scroll it, finish.
  Key @(0x11, 0x10, 0x34); Pump 900
  if (-not ((Get-Content $log -Tail 1) -match 'Overlays shown')) { throw 'Scrolling capture overlay did not appear; aborting.' }
  $pageWidth = $panel.ClientSize.Width
  Drag @($panelRect.X, $panelRect.Y) @(($panelRect.X + $pageWidth - 1), ($panelRect.Bottom - 1)); Pump 1200
  if (-not ((Get-Content $log -Tail 2) -match 'Scrolling capture of')) { throw 'Scrolling capture did not start; aborting.' }
  $max = $panel.DisplayRectangle.Height - $panel.ClientSize.Height
  for ($y = 250; $y -lt $max + 250; $y += 250) { $panel.AutoScrollPosition = New-Object Drawing.Point 0, ([Math]::Min($y, $max)); $panel.Refresh(); Pump 260 }
  Pump 600
  Key @(0x11, 0x10, 0x34); Pump 3000
  # The editor can open behind the stage (another process's window); bring it forward.
  [Wt]::SetForegroundWindow((EditorHandle)) | Out-Null; Pump 500
  "scrolling: $((Get-Content $log -Tail 3 | Select-String 'finished') -join '')"
  if ($null -eq (WindowRect 'Windshot')) { throw 'Editor not visible in front; aborting.' }
  $h = EditorHandle
  Key @(0x11, 0x30); MoveAbs ($work.Right - 40) ($work.Bottom - 40); Pump 800    # zoom to fit
  if ($null -eq (WindowRect 'Windshot')) { throw 'Editor not visible in front; aborting.' }
  $g = Grab (FrameBounds $h); $g.Save("$shots\raw-scrolling.png"); Compose $g 'Capture the whole page.' '04-scrolling'
  CloseEditor
} finally { $stage.Close() }

"editors open: $(@(Get-Process Windshot | Where-Object MainWindowTitle -eq 'Windshot').Count)"
Get-Content $log | Select-Object -Skip $script:logStart | Select-String 'Unhandled|failed|Couldn'
'done'
