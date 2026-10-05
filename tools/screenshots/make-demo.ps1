# Draws the fictional "Lumen" app used as screenshot content (physical pixels at 150%).
Add-Type -AssemblyName System.Drawing
$out = $PSScriptRoot
$k = 1.5   # display scale

function C([string]$hex) { [Drawing.ColorTranslator]::FromHtml($hex) }
function Brush([string]$hex) { New-Object Drawing.SolidBrush (C $hex) }
function F([float]$size, [string]$style = 'Regular') { New-Object Drawing.Font 'Segoe UI', ([float]($size * $k)), ([Drawing.FontStyle]$style), ([Drawing.GraphicsUnit]::Pixel) }
function RR($g, $brush, [float]$x, [float]$y, [float]$w, [float]$h, [float]$r, $pen = $null) {
    $x *= $k; $y *= $k; $w *= $k; $h *= $k; $r *= $k
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, 2*$r, 2*$r, 180, 90); $p.AddArc($x+$w-2*$r, $y, 2*$r, 2*$r, 270, 90)
    $p.AddArc($x+$w-2*$r, $y+$h-2*$r, 2*$r, 2*$r, 0, 90); $p.AddArc($x, $y+$h-2*$r, 2*$r, 2*$r, 90, 90); $p.CloseFigure()
    if ($brush) { $g.FillPath($brush, $p) }
    if ($pen) { $g.DrawPath($pen, $p) }
}
function T($g, [string]$s, $font, [string]$hex, [float]$x, [float]$y) { $g.DrawString($s, $font, (Brush $hex), [float]($x * $k), [float]($y * $k)) }
function Canvas([int]$w, [int]$h) {
    $bmp = New-Object Drawing.Bitmap ([int]($w * $k)), ([int]($h * $k))
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear((C '#f6f7fb'))
    return $bmp, $g
}

$people = @(
    @('Maya Chen', '#e07a5f'), @('Leo Park', '#3d85c6'), @('Priya Raman', '#8e7cc3'),
    @('Sam Ortiz', '#6aa84f'), @('Ava Brooks', '#e69138'), @('Noah Kim', '#45818e'))
$statuses = @{ 'Done' = '#2e9d5b'; 'In review' = '#b7791f'; 'In progress' = '#2f6fd0'; 'Blocked' = '#c0392b' }

function Sidebar($g, [int]$h) {
    $g.FillRectangle((Brush '#1f2440'), 0, 0, [int](230 * $k), [int]($h * $k))
    RR $g (Brush '#7c9cf5') 24 26 30 30 8
    T $g 'L' (F 16 'Bold') '#1f2440' 32 27
    T $g 'Lumen' (F 19 'Bold') '#ffffff' 64 25
    $y = 96
    foreach ($item in 'Overview', 'Projects', 'Team', 'Reports', 'Settings') {
        if ($item -eq 'Projects') { RR $g (Brush '#343b66') 14 ($y - 6) 202 38 8 }
        T $g $item (F 14.5) $(if ($item -eq 'Projects') { '#ffffff' } else { '#aab2d5' }) 30 $y
        $y += 46
    }
}

function Avatar($g, [string]$name, [string]$hex, [float]$x, [float]$y, [float]$d = 28) {
    $g.FillEllipse((Brush $hex), [float]($x * $k), [float]($y * $k), [float]($d * $k), [float]($d * $k))
    $initials = ($name.Split(' ') | ForEach-Object { $_[0] }) -join ''
    T $g $initials (F ($d * 0.38) 'Bold') '#ffffff' ($x + $d * 0.18) ($y + $d * 0.2)
}

function TaskRow($g, [float]$y, [string]$task, $person, [string]$status, [string]$due, [bool]$shade) {
    if ($shade) { $g.FillRectangle((Brush '#fafbfe'), [float](262 * $k), [float]($y * $k), [float](1040 * $k), [float](48 * $k)) }
    T $g $task (F 14) '#1d2433' 280 ($y + 12)
    Avatar $g $person[0] $person[1] 680 ($y + 10)
    T $g $person[0] (F 13.5) '#3b4256' 716 ($y + 13)
    $sw = 22 + 8.4 * $status.Length
    RR $g (New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(28, (C $statuses[$status])))) 900 ($y + 11) $sw 26 13
    T $g $status (F 12.5 'Bold') $statuses[$status] 910 ($y + 14)
    T $g $due (F 13.5) '#5d6478' 1150 ($y + 13)
    $g.DrawLine((New-Object Drawing.Pen (C '#eceef5'), ([float](1 * $k))), [float](262 * $k), [float](($y + 48) * $k), [float](1302 * $k), [float](($y + 48) * $k))
}

$tasks = @(
    @('Hero section copy', 0, 'Done', 'Oct 2'), @('Pricing page layout', 1, 'In review', 'Oct 6'),
    @('Checkout flow tests', 2, 'In progress', 'Oct 8'), @('Image compression pass', 3, 'Blocked', 'Oct 9'),
    @('Accessibility audit', 4, 'In progress', 'Oct 10'), @('Launch announcement', 5, 'In review', 'Oct 13'))

# ---- The board (1320 x 860 DIP) ----
$bmp, $g = Canvas 1320 860
Sidebar $g 860
T $g 'Projects  /' (F 13.5) '#7a8197' 262 26
T $g 'Website relaunch' (F 26 'Bold') '#141a2b' 260 46
$i = 0; foreach ($p in $people[0..3]) { Avatar $g $p[0] $p[1] (1060 + $i * 24) 54 32; $i++ }
RR $g (Brush '#2f6fd0') 1188 52 112 36 8
T $g 'Share' (F 14 'Bold') '#ffffff' 1222 59

# Stat cards.
$x = 262
foreach ($card in @(@('Tasks done', '128', '+12 this week', '#2e9d5b'), @('In review', '14', '3 waiting on you', '#b7791f'), @('Due this week', '9', '2 at risk', '#c0392b'))) {
    RR $g (Brush '#ffffff') $x 116 330 112 12 (New-Object Drawing.Pen (C '#e6e8f0'), ([float](1 * $k)))
    T $g $card[0] (F 13.5) '#5d6478' ($x + 20) 132
    T $g $card[1] (F 30 'Bold') '#141a2b' ($x + 18) 152
    T $g $card[2] (F 12.5 'Bold') $card[3] ($x + 20) 198
    $x += 355
}

# Weekly chart.
RR $g (Brush '#ffffff') 262 252 680 290 12 (New-Object Drawing.Pen (C '#e6e8f0'), ([float](1 * $k)))
T $g 'Weekly progress' (F 16 'Bold') '#141a2b' 282 268
T $g 'Tasks completed per day' (F 12.5) '#7a8197' 282 294
$days = 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'; $vals = 14, 22, 18, 31, 26, 9, 6
for ($d = 0; $d -lt 7; $d++) {
    $bh = $vals[$d] * 5.2; $bx = 312 + $d * 88
    $grad = New-Object Drawing.Drawing2D.LinearGradientBrush ((New-Object Drawing.PointF 0, ([float]((508 - $bh) * $k)))), ((New-Object Drawing.PointF 0, ([float](510 * $k)))), (C '#7c9cf5'), (C '#4b6fd8')
    RR $g $grad $bx (508 - $bh) 44 $bh 6
    T $g $days[$d] (F 12.5) '#7a8197' ($bx + 6) 514
}

# API key card (to redact).
RR $g (Brush '#ffffff') 966 252 336 290 12 (New-Object Drawing.Pen (C '#e6e8f0'), ([float](1 * $k)))
T $g 'Deploy settings' (F 16 'Bold') '#141a2b' 986 268
T $g 'Environment' (F 12.5) '#7a8197' 986 304
T $g 'Production' (F 14 'Bold') '#141a2b' 986 324
T $g 'API key' (F 12.5) '#7a8197' 986 364
RR $g (Brush '#f2f4f9') 986 386 296 40 8
T $g 'lm_demo_7Qx9Fk2Lp8Zr4Tn6' ([Drawing.Font]::new('Consolas', [float](14 * $k), [Drawing.GraphicsUnit]::Pixel)) '#141a2b' 998 396
T $g 'Webhook' (F 12.5) '#7a8197' 986 442
T $g 'hooks.lumen.example/deploy' (F 13.5) '#2f6fd0' 986 462
RR $g (Brush '#eef3ff') 986 494 140 30 8
T $g 'Rotate key' (F 12.5 'Bold') '#2f6fd0' 1006 500

# Task table.
RR $g (Brush '#ffffff') 262 566 1040 274 12 (New-Object Drawing.Pen (C '#e6e8f0'), ([float](1 * $k)))
T $g 'TASK' (F 11.5 'Bold') '#8a90a3' 280 580
T $g 'OWNER' (F 11.5 'Bold') '#8a90a3' 680 580
T $g 'STATUS' (F 11.5 'Bold') '#8a90a3' 900 580
T $g 'DUE' (F 11.5 'Bold') '#8a90a3' 1150 580
$y = 600; $n = 0
foreach ($t in $tasks[0..4]) { TaskRow $g $y $t[0] $people[$t[1]] $t[2] $t[3] ($n % 2 -eq 1); $y += 48; $n++ }
$g.Dispose(); $bmp.Save("$out\demo-board.png", [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()

# ---- A long changelog page for scrolling capture (900 x 3600 DIP) ----
$bmp, $g = Canvas 1200 2080
$g.Clear((C '#ffffff'))
T $g 'Lumen release notes' (F 30 'Bold') '#141a2b' 60 50
T $g 'Everything new in Lumen, newest first.' (F 15) '#5d6478' 62 100
$y = 160
$notes = @(
    @('3.8', 'Board filters', 'Filter any board by owner, status or due date, and save filters for later.'),
    @('3.7', 'Faster search', 'Search now covers comments and attachments, and returns results as you type.'),
    @('3.6', 'Weekly digest', 'A Monday summary of what moved last week, what is due and what is blocked.'),
    @('3.5', 'Dark theme', 'Lumen follows your system theme, or pick light or dark in Settings.'),
    @('3.4', 'Recurring tasks', 'Repeat a task daily, weekly or monthly, with its checklist reset each time.'),
    @('3.3', 'Calendar view', 'See tasks by due date on a month or week calendar, and drag to reschedule.'),
    @('3.2', 'Guest access', 'Invite clients to a single project with view or comment access.'),
    @('3.1', 'Time tracking', 'Start a timer on any task, or log time by hand, and export a weekly report.'),
    @('3.0', 'A fresh look', 'A redesigned sidebar, roomier boards and a new set of icons.'),
    @('2.9', 'Templates', 'Start projects from templates for launches, sprints, onboarding and more.'),
    @('2.8', 'Dependencies', 'Mark tasks as blocked by others, and see the chain on the timeline.'),
    @('2.7', 'Mobile widgets', 'Glance at today and this week from your phone home screen.'))
$colors = '#2f6fd0', '#8e7cc3', '#2e9d5b', '#e69138'
$i = 0
foreach ($note in $notes[0..6]) {
    RR $g (Brush '#f6f7fb') 60 $y 1080 230 14
    RR $g (Brush $colors[$i % 4]) 84 ($y + 24) 64 30 15
    T $g $note[0] (F 13.5 'Bold') '#ffffff' 100 ($y + 28)
    T $g $note[1] (F 20 'Bold') '#141a2b' 84 ($y + 70)
    T $g $note[2] (F 15) '#3b4256' 84 ($y + 108)
    for ($l = 0; $l -lt 3; $l++) { RR $g (Brush '#e6e8f0') 84 ($y + 150 + $l * 20) (880 - $l * 190) 8 4 }
    $y += 270; $i++
}
$g.Dispose(); $bmp.Save("$out\demo-long.png", [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
'done'
