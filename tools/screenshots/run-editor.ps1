. "$PSScriptRoot\lib-shots.ps1"
AssertFreshStart
$stage = Show-Stage
try {
  Open-BoardEditor

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
  GrabEditor 'raw-annotate' 'Annotate in seconds. Everything stays editable.' '01-annotate'

  Key @(0x4C); Park                                              # layers panel
  GrabEditor 'raw-layers' 'Layers, just like a photo editor.' '04-layers'
  Key @(0x4C); Pump 500

  # Clicked rather than toggled through automation, which would leave a keyboard focus outline.
  $beautify = FindIn $editor 'BeautifyButton'
  $b = $beautify.Current.BoundingRectangle
  Click @([int]($b.X + $b.Height / 2), [int]($b.Y + $b.Height / 2)); Park
  GrabEditor 'raw-beautify' 'Beautiful, and ready to share.' '06-beautify'
  $beautify.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle(); Pump 300
  CloseEditor
} finally { $stage.Close() }
"editors open: $(@(Get-Process Windshot | Where-Object MainWindowTitle -eq 'Windshot').Count)"
Get-Content $log | Select-Object -Skip $script:logStart | Select-String 'Unhandled|failed|Couldn'
'done'
