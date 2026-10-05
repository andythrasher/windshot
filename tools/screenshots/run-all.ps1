# Runs the editor screenshot scripts, each with known settings, then puts your settings back.
# Settings go in settings.json and Windshot restarts before each run (the scripts need a fresh
# "Started" in the log). Hands off the mouse and keyboard while it runs (about a minute).
#
#   powershell -File tools/screenshots/run-all.ps1 [-Runs editor,shapes,themes,scroll]
param([string[]]$Runs = @('editor', 'shapes', 'themes', 'scroll'))
$ErrorActionPreference = 'Stop'
$settings = "$env:LOCALAPPDATA\Windshot\settings.json"
$backup = "$settings.shots-backup"
$exe = "$env:LOCALAPPDATA\Programs\Windshot\Windshot.exe"
$log = "$env:LOCALAPPDATA\Windshot\windshot.log"

# Standard look and tools, so the shots don't depend on what was last used.
$standard = '{ "Appearance": { "Window": "Windshot", "Colors": "Windshot" }, "Editor": { "Shape": "Rectangle", "FillShapes": false, "HighlighterBlend": "Multiply" } }'
$runSettings = @{
    editor = $standard
    shapes = $standard
    scroll = $standard
    themes = '{ "Appearance": { "Window": "Moss", "Colors": "Moss" }, "Editor": { "Shape": "Rectangle", "FillShapes": false, "HighlighterBlend": "Multiply", "Font": "Segoe Print", "ShowLayers": false } }'
}

function Restart-Windshot {
    if (@(Get-Process Windshot -ErrorAction SilentlyContinue | Where-Object MainWindowTitle -eq 'Windshot').Count) { throw 'A Windshot editor is open; close it first.' }
    Get-Process Windshot -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    Start-Process $exe
    for ($i = 0; $i -lt 40 -and -not ((Get-Content $log -Tail 1) -match 'Started'); $i++) { Start-Sleep -Milliseconds 250 }
    Start-Sleep -Milliseconds 1500 # let it settle before any input
}

if (-not (Test-Path $backup)) { Copy-Item $settings $backup }
try {
    foreach ($run in $Runs) {
        Set-Content $settings $runSettings[$run] -Encoding UTF8
        Restart-Windshot
        "== $run"
        & powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\run-$run.ps1"
        if ($LASTEXITCODE -ne 0) { throw "run-$run failed" }
    }
} finally {
    Copy-Item $backup $settings -Force
    Remove-Item $backup
    Restart-Windshot
    'Your settings are back.'
}
