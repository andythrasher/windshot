# Installs (or updates) Windshot for the current user: a Release build in
# %LOCALAPPDATA%\Programs\Windshot with a Start menu shortcut, then starts it.
# Rerun it to update. No admin rights needed.
#
# Usage:
#   powershell -File tools/install.ps1              # build, install, start
#   powershell -File tools/install.ps1 -Uninstall   # remove the program, shortcut and startup entry
# Options: -Force closes Windshot even with editors open (unsaved edits are lost);
#          -NoLaunch installs without starting it.
# Settings, pins and the log in %LOCALAPPDATA%\Windshot are kept either way.
param([switch]$Uninstall, [switch]$Force, [switch]$NoLaunch)
$ErrorActionPreference = 'Stop'

$target = Join-Path $env:LOCALAPPDATA 'Programs\Windshot'
$exe = Join-Path $target 'Windshot.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Windshot.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

# Any running copy (installed or a dev build) has to go: files in use can't be replaced, and
# a second copy would just ask the first one to capture. Pins are saved, so they come back.
function Stop-Windshot {
    $running = @(Get-Process Windshot -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { return }
    $editors = @($running | Where-Object { $_.MainWindowTitle -eq 'Windshot' })
    if ($editors.Count -gt 0 -and -not $Force) {
        throw 'A Windshot editor is open. Save or close it and run this again (or use -Force to discard it).'
    }
    $running | Stop-Process -Force
    $running | ForEach-Object { $_.WaitForExit(5000) | Out-Null }
    'Stopped the running Windshot.'
}

function Get-RunEntry { (Get-ItemProperty $runKey -Name Windshot -ErrorAction SilentlyContinue).Windshot }

if ($Uninstall) {
    Stop-Windshot
    if ((Get-RunEntry) -like "*$target*") { Remove-ItemProperty $runKey -Name Windshot; 'Removed Start with Windows.' }
    if (Test-Path $shortcut) { Remove-Item $shortcut; 'Removed the Start menu shortcut.' }
    if (Test-Path $target) { Remove-Item $target -Recurse -Force; "Removed $target." }
    "Settings, pins and the log are still in $env:LOCALAPPDATA\Windshot; delete that folder to remove them too."
    return
}

# Self-contained (no .NET install needed) and ReadyToRun (precompiled, so editors open quickly).
$project = Join-Path $PSScriptRoot '..\src\Windshot\Windshot.csproj'
$staging = Join-Path $env:TEMP 'windshot-publish'
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
'Building the Release version...'
dotnet publish $project -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o $staging -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "The build failed (exit code $LASTEXITCODE)." }

Stop-Windshot
New-Item -ItemType Directory -Force $target | Out-Null
# Mirror, so files dropped from a newer build don't linger. Robocopy codes below 8 mean success.
robocopy $staging $target /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying to $target failed (robocopy exit code $LASTEXITCODE)." }
Remove-Item $staging -Recurse -Force
"Installed to $target."

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $target
$link.IconLocation = "$exe,0"
$link.Description = 'Screenshots with an editor that keeps annotations editable'
$link.Save()
'Added Windshot to the Start menu.'

# If Start with Windows is on (for this or a dev build), point it here.
if (Get-RunEntry) {
    Set-ItemProperty $runKey -Name Windshot -Value "`"$exe`" --autostart"
    'Start with Windows now starts this copy.'
}

if (-not $NoLaunch) {
    Start-Process $exe
    'Started Windshot; it''s in the tray.'
}
