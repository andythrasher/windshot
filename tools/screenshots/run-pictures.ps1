# A picture with a style, in the standard theme: the Lumen phone pasted in, moved to the right,
# tilted, and given a white outline and a shadow. Ends with the Style menu open.
# Uses the clipboard to paste; whatever text, image or files were on it are put back.
. "$PSScriptRoot\lib-shots.ps1"
AssertFreshStart

# Keep the clipboard to put back afterwards.
$saved = $null
if ([Windows.Forms.Clipboard]::ContainsText()) { $saved = @('text', [Windows.Forms.Clipboard]::GetText()) }
elseif ([Windows.Forms.Clipboard]::ContainsImage()) { $saved = @('image', [Windows.Forms.Clipboard]::GetImage()) }
elseif ([Windows.Forms.Clipboard]::ContainsFileDropList()) { $saved = @('files', [Windows.Forms.Clipboard]::GetFileDropList()) }

# Finds an element by automation ID in any of Windshot's windows (the Style menu is a window of its own).
function FindAnywhere($id) {
  $windshot = @(Get-Process Windshot).Id
  foreach ($w in $A::RootElement.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)) {
    if ($windshot -notcontains $w.Current.ProcessId) { continue }
    $el = $w.FindFirst([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new($A::AutomationIdProperty, $id))
    if ($el) { return $el }
  }
  throw "$id not found; aborting."
}

$stage = Show-Stage
try {
  Open-BoardEditor

  # Paste the phone (as PNG, so it keeps its transparent corners). It comes in centered.
  $png = "$shots\demo-phone.png"
  $data = New-Object Windows.Forms.DataObject
  $data.SetData('PNG', $false, (New-Object IO.MemoryStream (,[IO.File]::ReadAllBytes($png))))
  $data.SetImage([Drawing.Image]::FromFile($png))
  [Windows.Forms.Clipboard]::SetDataObject($data, $true)
  Key @(0x11, 0x56); Pump 1200
  Guard

  # Over the deploy card and the task table, tilted 6 degrees by its rotate handle.
  Drag (B 990 645) (B 1700 800); Pump 300
  Drag (B 1700 428) (B 1739 430); Pump 300
  Guard

  # A white outline and a shadow, from the Style menu, left open for the shot.
  ClickControl 'StyleButton'; Pump 600
  (FindAnywhere 'OutlineSlider').GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern).SetValue(3); Pump 200
  (FindAnywhere 'ShadowSlider').GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern).SetValue(7); Pump 300
  Park
  GrabEditor 'raw-pictures' 'Add pictures, and make anything stand out.' '10-pictures'
  Key @(0x1B); Pump 300
  CloseEditor
} finally {
  $stage.Close()
  switch ($saved[0]) {
    'text' { [Windows.Forms.Clipboard]::SetText($saved[1]) }
    'image' { [Windows.Forms.Clipboard]::SetImage($saved[1]) }
    'files' { [Windows.Forms.Clipboard]::SetFileDropList($saved[1]) }
    default { [Windows.Forms.Clipboard]::Clear() }
  }
}
"editors open: $(@(Get-Process Windshot | Where-Object MainWindowTitle -eq 'Windshot').Count)"
Get-Content $log | Select-Object -Skip $script:logStart | Select-String 'Unhandled|failed|Couldn'
'done'
