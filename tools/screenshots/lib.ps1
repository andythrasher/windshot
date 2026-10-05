# Shared helpers for driving Winshot in tests. Dot-source this file.
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Wt {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int d, UIntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr FindWindow(string c, string t);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  public struct RECT { public int L, T, R, B; }
}
'@
[Wt]::SetProcessDpiAwarenessContext([IntPtr]-4) | Out-Null
$script:vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
$script:log = "$env:LOCALAPPDATA\Windshot\windshot.log"
function MoveAbs($x, $y) { [Wt]::mouse_event(0xC001, [int](($x - $vs.X) * 65535 / ($vs.Width - 1)), [int](($y - $vs.Y) * 65535 / ($vs.Height - 1)), 0, [UIntPtr]::Zero) }
function Key([byte[]]$vks) { foreach ($k in $vks) { [Wt]::keybd_event($k,0,0,[UIntPtr]::Zero) }; [array]::Reverse($vks); foreach ($k in $vks) { [Wt]::keybd_event($k,0,2,[UIntPtr]::Zero) }; Start-Sleep -m 150 }
function Click($a) { MoveAbs $a[0] $a[1]; Start-Sleep -m 60; [Wt]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Start-Sleep -m 40; [Wt]::mouse_event(4,0,0,0,[UIntPtr]::Zero); Start-Sleep -m 300 }
function Wheel($a, $notches) { MoveAbs $a[0] $a[1]; Start-Sleep -m 60; [Wt]::mouse_event(0x800,0,0,120 * $notches,[UIntPtr]::Zero); Start-Sleep -m 300 }
function Drag($a, $b) {
  MoveAbs $a[0] $a[1]; Start-Sleep -m 50; [Wt]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
  for ($i = 1; $i -le 8; $i++) { MoveAbs ($a[0] + ($b[0]-$a[0])*$i/8) ($a[1] + ($b[1]-$a[1])*$i/8); Start-Sleep -m 25 }
  [Wt]::mouse_event(4,0,0,0,[UIntPtr]::Zero); Start-Sleep -m 300
}
function Shot($x, $y, $w, $h, $path, [double]$scale = 0.5) {
  $bmp = New-Object Drawing.Bitmap $w, $h; $g = [Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
  $out = New-Object Drawing.Bitmap $bmp, ([int]($w * $scale)), ([int]($h * $scale)); $out.Save($path)
}
function Center($el) { $b = $el.Current.BoundingRectangle; @([int]($b.X + $b.Width / 2), [int]($b.Y + $b.Height / 2)) }
function WindowRect($title) {
  $h = [Wt]::FindWindow([NullString]::Value, $title); if ($h -eq [IntPtr]::Zero) { return $null }
  # A crashed editor can leave a hidden window with the same title: only a visible, foreground one counts.
  if (-not [Wt]::IsWindowVisible($h) -or [Wt]::GetForegroundWindow() -ne $h) { return $null }
  if (Get-Content $log | Select-Object -Skip $script:logStart | Select-String 'Unhandled|Opening editor failed') { return $null }
  $r = New-Object Wt+RECT; [Wt]::GetWindowRect($h, [ref]$r) | Out-Null; return $r
}
# Guards: never send input unless the app is really running and responding.
$script:logStart = 0
function AssertFreshStart {
  if (-not ((Get-Content $log -Tail 1) -match 'Started')) { throw 'Windshot did not log a fresh start; aborting before sending any input.' }
  # Errors are only looked for from here on.
  $script:logStart = @(Get-Content $log).Count - 1
}
function CaptureRegion($a, $b) {
  Key @(0x11,0x10,0x32); Start-Sleep -m 500
  if (-not ((Get-Content $log -Tail 1) -match 'Overlays shown')) { throw 'Capture overlay did not appear; aborting.' }
  Drag $a $b; Start-Sleep -m 1500
  $r = WindowRect 'Windshot'; if ($null -eq $r) { throw 'Editor window not found; aborting.' }
  return $r
}
