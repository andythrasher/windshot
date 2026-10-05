. "$PSScriptRoot\lib-shots.ps1"
AssertFreshStart
$stage = Show-Stage
try {
  # Capture the whole board.
  Start-Process $exe; Pump 1500
  if (-not ((Get-Content $log -Tail 1) -match 'Overlays shown')) { throw 'Capture overlay did not appear; aborting.' }
  Drag @($boardRect.X, $boardRect.Y) @($boardRect.Right, $boardRect.Bottom); Pump 2000
  if ($null -eq (WindowRect 'Windshot')) { throw 'Editor not visible in front; aborting.' }
  $h = EditorHandle
  # At 100% the image is centered in the canvas area (below the toolbar): board pixel -> screen.
  $frame = FrameBounds $h; $bar = (FindIn $h 'Toolbar').Current.BoundingRectangle
  if ($bar.Height -le 0) { throw 'Toolbar not found; aborting.' }
  $iw = $boardRect.Width; $ih = $boardRect.Height
  $ix = [int]($frame.X + ($frame.Width - $iw) / 2); $iy = [int]($bar.Bottom + ($frame.Bottom - $bar.Bottom - $ih) / 2)
  function B($bx, $by) { @(($ix + $bx), ($iy + $by)) }
  function Guard { if ($null -eq (WindowRect 'Windshot')) { throw 'Editor not visible in front; aborting.' } }
  # Parks the mouse on the stage, away from the editor, so no hover effects or tooltips show.
  function Park { MoveAbs ($work.Right - 40) ($work.Bottom - 40); Pump 400 }
  $corner = B 0 0; if (-not $frame.Contains($corner[0], $corner[1])) { throw 'Image position is outside the editor; aborting.' }

  Key @(0x42); Drag (B 1470 572) (B 1932 646)                    # pixelate the API key
  Guard
  Key @(0x52); Drag (B 1446 164) (B 1962 352)                    # box around "Due this week"
  Key @(0x48); Drag (B 418 1152) (B 668 1152)                    # highlight a task
  Key @(0x41); Drag (B 1110 600) (B 945 540)                     # arrow to Thursday's bar
  Key @(0x54); Click (B 1090 610); Pump 300
  Guard
  [Windows.Forms.SendKeys]::SendWait('Best day yet{!}'); Pump 200; Key @(0x1B)
  Key @(0x4E); foreach ($y in 1008, 1080, 1224) { Click (B 369 $y) }   # numbered steps
  Key @(0x56); Key @(0x1B); Park
  Guard
  $g = Grab (FrameBounds $h); $g.Save("$shots\raw-annotate.png"); Compose $g 'Annotate in seconds. Everything stays editable.' '01-annotate'

  Key @(0x4C); Park                                              # layers panel
  Guard
  $g = Grab (FrameBounds $h); $g.Save("$shots\raw-layers.png"); Compose $g 'Layers, just like a photo editor.' '03-layers'
  Key @(0x4C); Pump 500

  # Clicked rather than toggled through automation, which would leave a keyboard focus outline.
  $beautify = FindIn $h 'BeautifyButton'
  $b = $beautify.Current.BoundingRectangle
  Click @([int]($b.X + $b.Height / 2), [int]($b.Y + $b.Height / 2)); Park
  Guard
  $g = Grab (FrameBounds $h); $g.Save("$shots\raw-beautify.png"); Compose $g 'Beautiful, and ready to share.' '05-beautify'
  $beautify.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle(); Pump 300
  CloseEditor
} finally { $stage.Close() }
"editors open: $(@(Get-Process Windshot | Where-Object MainWindowTitle -eq 'Windshot').Count)"
Get-Content $log | Select-Object -Skip $script:logStart | Select-String 'Unhandled|failed|Couldn'
'done'
