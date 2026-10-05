# Helpers for the Store screenshots: a demo "stage" behind Windshot, grabbing windows, and
# composing each grab onto a 3840x2160 branded background with a headline.
. "$PSScriptRoot\lib.ps1"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Dwm {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
}
'@
$script:shots = $PSScriptRoot
$script:exe = "$env:LOCALAPPDATA\Programs\Windshot\Windshot.exe"
$A = [Windows.Automation.AutomationElement]
function Pump($ms) { $end = [DateTime]::Now.AddMilliseconds($ms); while ([DateTime]::Now -lt $end) { [Windows.Forms.Application]::DoEvents(); Start-Sleep -m 10 } }

$work = [Windows.Forms.Screen]::PrimaryScreen.WorkingArea

function Wallpaper([int]$w, [int]$h) {
  $bmp = New-Object Drawing.Bitmap $w, $h
  $g = [Drawing.Graphics]::FromImage($bmp)
  $brush = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.Point 0, 0), (New-Object Drawing.Point $w, $h), ([Drawing.Color]::FromArgb(255, 214, 226, 246)), ([Drawing.Color]::FromArgb(255, 232, 222, 244))
  $g.FillRectangle($brush, 0, 0, $w, $h); $g.Dispose()
  return $bmp
}

# The board, as a window-like card centered on a soft wallpaper filling the work area.
function Show-Stage {
  $form = New-Object Windows.Forms.Form
  $form.FormBorderStyle = 'None'; $form.StartPosition = 'Manual'; $form.ShowInTaskbar = $false
  $form.Bounds = $work
  $bg = Wallpaper $work.Width $work.Height
  $board = [Drawing.Image]::FromFile("$shots\demo-board.png")
  $g = [Drawing.Graphics]::FromImage($bg)
  $x = [int](($work.Width - $board.Width) / 2); $y = [int](($work.Height - $board.Height) / 2)
  for ($i = 12; $i -ge 1; $i--) { $g.FillRectangle((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(4, 30, 30, 60))), $x - $i * 2, $y - $i * 2 + 12, $board.Width + $i * 4, $board.Height + $i * 4) }
  $g.DrawImage($board, $x, $y, $board.Width, $board.Height); $g.Dispose()
  $form.BackgroundImage = $bg
  $form.Show(); Pump 500
  $script:boardRect = New-Object Drawing.Rectangle ($work.X + $x), ($work.Y + $y), $board.Width, $board.Height
  return $form
}

# The long changelog in a scrolling panel, for scrolling capture.
function Show-LongStage {
  $form = New-Object Windows.Forms.Form
  $form.FormBorderStyle = 'None'; $form.StartPosition = 'Manual'; $form.ShowInTaskbar = $false
  $form.Bounds = $work
  $form.BackgroundImage = Wallpaper $work.Width $work.Height
  $long = [Drawing.Image]::FromFile("$shots\demo-long.png")
  $panel = New-Object Windows.Forms.Panel
  $panel.AutoScroll = $true; $panel.BackColor = [Drawing.Color]::White
  $panel.Size = New-Object Drawing.Size ($long.Width + 30), 1500
  $panel.Location = New-Object Drawing.Point ([int](($work.Width - $panel.Width) / 2)), ([int](($work.Height - 1500) / 2))
  $pic = New-Object Windows.Forms.PictureBox
  $pic.Image = $long; $pic.Size = $long.Size; $pic.Location = New-Object Drawing.Point 0, 0
  $panel.Controls.Add($pic); $form.Controls.Add($panel)
  $form.Show(); Pump 500
  $script:panel = $panel
  $script:panelRect = $panel.RectangleToScreen($panel.ClientRectangle)
  return $form
}

# A window's visible bounds (without the invisible resize border).
function FrameBounds([IntPtr]$h) {
  $r = New-Object Dwm+RECT; [Dwm]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null
  New-Object Drawing.Rectangle $r.L, $r.T, ($r.R - $r.L), ($r.B - $r.T)
}

function Grab([Drawing.Rectangle]$rect) {
  $bmp = New-Object Drawing.Bitmap $rect.Width, $rect.Height
  $g = [Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($rect.Location, [Drawing.Point]::Empty, $rect.Size); $g.Dispose()
  return $bmp
}

function RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
  $p = New-Object Drawing.Drawing2D.GraphicsPath
  if ($r -le 0) { $p.AddRectangle((New-Object Drawing.RectangleF $x, $y, $w, $h)); return $p }
  $p.AddArc($x, $y, 2*$r, 2*$r, 180, 90); $p.AddArc($x+$w-2*$r, $y, 2*$r, 2*$r, 270, 90)
  $p.AddArc($x+$w-2*$r, $y+$h-2*$r, 2*$r, 2*$r, 0, 90); $p.AddArc($x, $y+$h-2*$r, 2*$r, 2*$r, 90, 90); $p.CloseFigure()
  return $p
}

# Places a grab on the branded background under a headline, scaled to fit, with rounded
# corners (Windows 11 windows are rounded; what showed behind the corners is cut away) and a shadow.
function Compose([Drawing.Bitmap]$grab, [string]$headline, [string]$name, [double]$cornerRadius = 12) {
  # (PowerShell names ignore case, so these must not be $W/$H next to $w/$h.)
  $canvasW = 3840; $canvasH = 2160
  $canvas = New-Object Drawing.Bitmap $canvasW, $canvasH
  $g = [Drawing.Graphics]::FromImage($canvas)
  $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.TextRenderingHint = 'AntiAliasGridFit'
  $bg = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.Point 0, 0), (New-Object Drawing.Point $canvasW, $canvasH), ([Drawing.Color]::FromArgb(255, 52, 102, 196)), ([Drawing.Color]::FromArgb(255, 128, 90, 196))
  $g.FillRectangle($bg, 0, 0, $canvasW, $canvasH)

  $font = New-Object Drawing.Font 'Segoe UI Variable Display', 104, ([Drawing.FontStyle]::Bold), ([Drawing.GraphicsUnit]::Pixel)
  if ($font.Name -ne 'Segoe UI Variable Display') { $font = New-Object Drawing.Font 'Segoe UI', 104, ([Drawing.FontStyle]::Bold), ([Drawing.GraphicsUnit]::Pixel) }
  $fmt = New-Object Drawing.StringFormat; $fmt.Alignment = 'Center'
  $g.DrawString($headline, $font, [Drawing.Brushes]::White, (New-Object Drawing.RectangleF 0, 120, $canvasW, 160), $fmt)

  $maxW = 3360; $maxH = 1640; $top = 380
  $s = [Math]::Min(1.0, [Math]::Min($maxW / $grab.Width, $maxH / $grab.Height))
  $dw = [int]($grab.Width * $s); $dh = [int]($grab.Height * $s)
  $x = [int](($canvasW - $dw) / 2); $y = $top + [int](($maxH - $dh) / 2)
  $r = [float]($cornerRadius * $s)
  for ($i = 20; $i -ge 1; $i--) {
    $shadow = RoundedPath ($x - $i * 2) ($y - $i * 2 + 24) ($dw + $i * 4) ($dh + $i * 4) ($r + $i * 2)
    $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(5, 10, 10, 40))), $shadow)
  }
  $clip = RoundedPath $x $y $dw $dh $r
  $g.SetClip($clip)
  $g.DrawImage($grab, $x, $y, $dw, $dh)
  $g.ResetClip(); $g.Dispose()
  $path = Join-Path $shots "$name.png"
  $canvas.Save($path, [Drawing.Imaging.ImageFormat]::Png); $canvas.Dispose()
  "saved $name.png"
}

function EditorHandle { [Wt]::FindWindow([NullString]::Value, 'Windshot') }
function CloseEditor { $h = EditorHandle; if ($h -ne [IntPtr]::Zero) { $A::FromHandle($h).GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close(); Pump 600 } }
function FindIn([IntPtr]$h, [string]$id) { $A::FromHandle($h).FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition $A::AutomationIdProperty, $id)) }
