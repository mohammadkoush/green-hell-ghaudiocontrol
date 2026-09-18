# Build GHAudioControl.dll and (optionally) deploy it into Green Hell's BepInEx plugins folder.
#
# Uses the stock .NET Framework csc.exe, so no Visual Studio or SDK is needed. Reference assemblies
# are taken straight from the game install, which means the build always matches the installed
# game version.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1            # build + deploy
#   powershell -ExecutionPolicy Bypass -File build.ps1 -NoDeploy  # build only
param(
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Green Hell',
    [switch]$NoDeploy
)
$ErrorActionPreference = 'Stop'

$managed = Join-Path $GameDir 'GH_Data\Managed'
$core    = Join-Path $GameDir 'BepInEx\core'
$csc     = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$srcDir  = $PSScriptRoot
$outDir  = Join-Path $srcDir 'build'
$outDll  = Join-Path $outDir 'GHAudioControl.dll'

foreach ($p in @($managed, $core, $csc)) {
    if (-not (Test-Path $p)) { throw "Not found: $p" }
}
New-Item -ItemType Directory -Force $outDir | Out-Null

$refs = @(
    (Join-Path $managed 'Assembly-CSharp.dll')
    (Join-Path $managed 'UnityEngine.dll')
    (Join-Path $managed 'UnityEngine.CoreModule.dll')
    (Join-Path $managed 'UnityEngine.InputLegacyModule.dll')
    # IMGUI: the halo minimap is drawn with OnGUI, which owes the game's UI nothing and cannot be
    # broken by it. That independence is why the minimap was the first thing built.
    (Join-Path $managed 'UnityEngine.IMGUIModule.dll')
    # GUIStyle's TextAnchor and FontStyle live here, not in IMGUIModule - needed by the settings menu
    (Join-Path $managed 'UnityEngine.TextRenderingModule.dll')
    # PhysicsModule: Collider, stripped off the marker quads before they go near the player's face.
    (Join-Path $managed 'UnityEngine.PhysicsModule.dll')
    # ImageConversionModule: ImageConversion.LoadImage, which turns the icon PNGs on disk into
    # textures at runtime. This is what lets icons ship as loose files instead of an AssetBundle.
    (Join-Path $managed 'UnityEngine.ImageConversionModule.dll')
    (Join-Path $managed 'UnityEngine.AudioModule.dll')
    (Join-Path $core    'BepInEx.dll')
    (Join-Path $core    '0Harmony.dll')
)
foreach ($r in $refs) { if (-not (Test-Path $r)) { throw "Missing reference: $r" } }

$sources = @(Get-ChildItem $srcDir -Filter *.cs | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) { throw "No .cs sources found in $srcDir" }

$argList = New-Object 'System.Collections.Generic.List[string]'
$argList.Add('/nologo')
$argList.Add('/target:library')
$argList.Add('/optimize+')
$argList.Add('/warn:3')
$argList.Add('/out:' + $outDll)
foreach ($r in $refs)    { $argList.Add('/reference:' + $r) }
foreach ($s in $sources) { $argList.Add($s) }

Write-Host "Compiling GHAudioControl..." -ForegroundColor Cyan
& $csc $argList.ToArray()
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED (csc exit $LASTEXITCODE)" -ForegroundColor Red; exit 1 }
Write-Host "Built $outDll" -ForegroundColor Green

if ($NoDeploy) { Write-Host "Skipping deploy (-NoDeploy)." -ForegroundColor Yellow; exit 0 }

$dest = Join-Path $GameDir 'BepInEx\plugins\GHAudioControl'
New-Item -ItemType Directory -Force $dest | Out-Null

$running = @(Get-Process -Name 'GH' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host ""
    Write-Host "BUILD OK, DEPLOY SKIPPED: Green Hell is running (PID $($running[0].Id))." -ForegroundColor Yellow
    Write-Host "The game has the old DLL locked. Close the game, then re-run this script." -ForegroundColor Yellow
    Write-Host "The fresh build is waiting at: $outDll" -ForegroundColor Cyan
    exit 2
}
try {
    Copy-Item $outDll (Join-Path $dest 'GHAudioControl.dll') -Force -ErrorAction Stop
} catch {
    Write-Host ""
    Write-Host "BUILD OK, DEPLOY FAILED: could not overwrite the deployed DLL." -ForegroundColor Yellow
    Write-Host "This almost always means Green Hell is still running. Close it and re-run." -ForegroundColor Yellow
    exit 2
}

$icons = Join-Path $srcDir 'icons'
if (Test-Path $icons) {
    $iconDest = Join-Path $dest 'icons'
    New-Item -ItemType Directory -Force $iconDest | Out-Null
    Copy-Item (Join-Path $icons '*.png') $iconDest -Force
    Write-Host ("Copied " + @(Get-ChildItem $iconDest -Filter *.png).Count + " icon(s)") -ForegroundColor Green
}
Write-Host "Deployed -> $dest" -ForegroundColor Green
Write-Host "" 
Write-Host "K opens the sound panel: Ambient and True animal voice, a switch per animal." -ForegroundColor Yellow
Write-Host ""





