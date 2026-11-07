# install.ps1 -- copies Animation & Timing from dist\ to %LOCALAPPDATA%\Programs\AnimTiming and puts its icon on the desktop.
# The in-game script is NOT put into any game here: the app's own button does that for the game it finds.
#   powershell -ExecutionPolicy Bypass -File install.ps1 [-From <folder with the built files>]
param([string]$From = "")
# from a source folder the built files are in dist\; from the download zip they sit next to this script
if ($From -eq "") { $From = if (Test-Path (Join-Path $PSScriptRoot "dist\AnimTiming.exe")) { Join-Path $PSScriptRoot "dist" } else { $PSScriptRoot } }
$ErrorActionPreference = "Stop"
$to = Join-Path $env:LOCALAPPDATA "Programs\AnimTiming"
New-Item -ItemType Directory -Force $to | Out-Null
foreach ($f in "AnimTiming.exe", "anim_timing.lua", "anim_timing.ico") {
    if (Test-Path (Join-Path $From $f)) { Copy-Item (Join-Path $From $f) (Join-Path $to $f) -Force }
}
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath("Desktop")) "Animation & Timing.lnk"))
$lnk.TargetPath = Join-Path $to "AnimTiming.exe"
$lnk.WorkingDirectory = $to
$lnk.IconLocation = (Join-Path $to "anim_timing.ico") + ",0"
$lnk.Description = "Freeze, step and nudge the player's animations in RE Engine games; tune Visceral RE8's clips and timings"
$lnk.Save()
Write-Output "Installed to $to, icon on the desktop."
