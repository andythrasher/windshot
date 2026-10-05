# Builds the Microsoft Store package (MSIX) into artifacts\store, ready to upload to Partner
# Center, which signs it. Optionally installs a test copy on this PC first.
#
# Usage:
#   powershell -File tools/package.ps1                # build artifacts\store\Windshot_<version>_x64.msix
#   powershell -File tools/package.ps1 -TestInstall   # ...and install a test copy for this user
#   powershell -File tools/package.ps1 -Uninstall     # remove the test copy
#
# The package is unsigned until the Store signs it, and Windows only installs unsigned desktop
# apps from an unpacked folder with Developer Mode on (Settings > System > For developers).
# So -TestInstall unpacks it into artifacts\test-install and registers that, under its own
# name (Windshot.Dev) so it never collides with the Store version. It and the unpackaged app
# (tools/install.ps1) shouldn't run at once: both want the same hotkeys, so the unpackaged one
# is stopped first.
# Options: -Force closes Windshot even with editors open (unsaved edits are lost).
param([switch]$TestInstall, [switch]$Uninstall, [switch]$Force)
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $root 'src\Windshot\Windshot.csproj'
$out = Join-Path $root 'artifacts\store'
$testName = 'Windshot.Dev'
$testLayout = Join-Path $root 'artifacts\test-install'

function Stop-Windshot {
    $running = @(Get-Process Windshot -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { return }
    if (@($running | Where-Object { $_.MainWindowTitle -eq 'Windshot' }).Count -gt 0 -and -not $Force) {
        throw 'A Windshot editor is open. Save or close it and run this again (or use -Force to discard it).'
    }
    $running | Stop-Process -Force
    $running | ForEach-Object { $_.WaitForExit(5000) | Out-Null }
    'Stopped the running Windshot.'
}

function Remove-TestCopy {
    $installed = Get-AppxPackage -Name $testName
    if ($installed) {
        Stop-Windshot
        $installed | Remove-AppxPackage
        "Removed the test copy ($($installed.Version))."
    }
    if (Test-Path $testLayout) { Remove-Item $testLayout -Recurse -Force }
}

function Test-DeveloperMode {
    $key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
    (Get-ItemProperty $key -Name AllowDevelopmentWithoutDevLicense -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense -eq 1
}

if ($Uninstall) {
    Remove-TestCopy
    return
}
if ($TestInstall -and -not (Test-DeveloperMode)) {
    throw 'Installing a test copy needs Developer Mode: Settings > System > For developers > Developer Mode. Turn it on and run this again.'
}

'Building the Store package...'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish $project -c Release -r win-x64 -p:Platform=x64 -p:StorePackage=true -p:PublishReadyToRun=true "-p:AppxPackageDir=$out\" -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "The build failed (exit code $LASTEXITCODE)." }
$msix = Get-ChildItem $out -Recurse -Filter 'Windshot_*.msix' | Select-Object -First 1
if (-not $msix) { throw "No package was produced in $out." }
# Keep just the package; the generated sideloading scripts and runtime copies aren't needed.
$final = Join-Path $out $msix.Name
Move-Item $msix.FullName $final
Get-ChildItem $out -Directory | Remove-Item -Recurse -Force
'Built {0} ({1:N1} MB). Upload this to Partner Center.' -f $final, ((Get-Item $final).Length / 1MB)

if (-not $TestInstall) { return }

# Unpack the exact package being uploaded, rename it, and register the folder.
$makeappx = Get-ChildItem (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools') -Recurse -Filter makeappx.exe |
    Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeappx) { throw 'makeappx.exe not found; build the project once so the Windows SDK build tools are restored.' }
Remove-TestCopy
Stop-Windshot
& $makeappx.FullName unpack /p $final /d $testLayout /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Unpacking the package failed.' }
$manifestPath = Join-Path $testLayout 'AppxManifest.xml'
[xml]$manifest = Get-Content $manifestPath -Raw
$manifest.Package.Identity.Name = $testName
$manifest.Save($manifestPath)
Add-AppxPackage -Register $manifestPath
$installed = Get-AppxPackage -Name $testName
"Installed the test copy ($($installed.Version)). Start it from the Start menu (Windshot), or:"
"  explorer.exe shell:AppsFolder\$($installed.PackageFamilyName)!App"
"Its settings, pins and log are in $env:LOCALAPPDATA\Packages\$($installed.PackageFamilyName)\LocalState."
