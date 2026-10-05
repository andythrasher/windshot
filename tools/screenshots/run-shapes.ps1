# Shapes and stickers, in the standard theme: an ellipse, a filled bar over the API key, and
# stickers placed with clicks. Ends with the ellipse selected, so the options bar shows the shapes.
. "$PSScriptRoot\lib-shots.ps1"
AssertFreshStart
$stage = Show-Stage
try {
  Open-BoardEditor

  Key @(0x52); Pump 300                                          # Shapes tool
  ClickControl 'ShapeEllipseButton'
  Key @(0x31); Drag (B 1458 280) (B 1600 342)                    # coral ellipse around "2 at risk"
  Key @(0x1B)                                                    # deselect, or the next shape button converts it
  Guard
  ClickControl 'ShapeRectangleButton'
  Key @(0x37); Key @(0x46)                                       # ink, filled
  Drag (B 1488 584) (B 1916 630)                                 # cover the API key
  Key @(0x1B); Key @(0x46)                                       # deselect, then fill back off
  Guard

  # Stickers, a bit larger than the default, each in its own color.
  ClickControl 'ShapeStickerButton'; Pump 300
  ClickNamed 'Checkmark'
  Key @(0x1B); foreach ($i in 1..2) { Key @(0xDD) }
  Key @(0x34); Click (B 1468 934); Key @(0x1B)                   # sage checkmark by "Done"
  ClickControl 'ShapeStickerButton'; Pump 300; ClickNamed 'Warning'
  Key @(0x32); Click (B 1500 1150); Key @(0x1B)                  # tangerine warning by "Blocked"
  ClickControl 'ShapeStickerButton'; Pump 300; ClickNamed 'Star'
  Key @(0x33); Click (B 897 470); Key @(0x1B)                    # marigold star over Thursday
  ClickControl 'ShapeStickerButton'; Pump 300; ClickNamed 'Thumbs up'
  Key @(0x35); Click (B 596 310); Key @(0x1B)                    # ocean thumbs up by "+12 this week"
  Guard

  # Select the ellipse (by its edge) so the options bar shows the shape tools.
  Key @(0x56); Click (B 1458 311); Park
  GrabEditor 'raw-shapes' 'Shapes and stickers, outlined or filled.' '02-shapes'
  CloseEditor
} finally { $stage.Close() }
"editors open: $(@(Get-Process Windshot | Where-Object MainWindowTitle -eq 'Windshot').Count)"
Get-Content $log | Select-Object -Skip $script:logStart | Select-String 'Unhandled|failed|Couldn'
'done'
