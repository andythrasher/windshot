# A supporter theme: run with settings.json set to the Moss window and colors and the Segoe
# Print font (run-all.ps1 does that). Ends with the text selected and the layers panel open.
. "$PSScriptRoot\lib-shots.ps1"
AssertFreshStart
$stage = Show-Stage
try {
  Open-BoardEditor

  Key @(0x52); Drag (B 384 66) (B 734 130)                       # box around the title
  Key @(0x48); Drag (B 418 1008) (B 612 1008)                    # highlight a task
  Key @(0x41); Drag (B 1160 470) (B 1010 545)                    # arrow to Friday's bar
  Guard
  Key @(0x54); Click (B 1120 455); Pump 300
  [Windows.Forms.SendKeys]::SendWait('Ship it{!}'); Pump 200; Key @(0x1B)
  Guard
  Key @(0x52); Pump 300
  ClickControl 'ShapeStickerButton'; Pump 300; ClickNamed 'Heart'
  Key @(0x1B); foreach ($i in 1..2) { Key @(0xDD) }
  Key @(0x31); Click (B 778 98); Key @(0x1B)                     # a rust heart beside the title
  Guard

  Key @(0x4C); Pump 400                                          # layers panel
  Key @(0x56); Click (B 1150 455); Park                          # select the text: Font shows
  GrabEditor 'raw-themes' 'Make it yours with supporter themes.' '07-themes'
  CloseEditor
} finally { $stage.Close() }
"editors open: $(@(Get-Process Windshot | Where-Object MainWindowTitle -eq 'Windshot').Count)"
Get-Content $log | Select-Object -Skip $script:logStart | Select-String 'Unhandled|failed|Couldn'
'done'
