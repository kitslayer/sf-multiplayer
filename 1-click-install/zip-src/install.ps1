# ===================================================================
#  sf-multiplayer  -  Stick Fight Oracle  -  1-Click Installer
#  kitslayer
#
#  Finds Stick Fight in any Steam library, BACKS UP your original
#  Assembly-CSharp.dll, and copies the contents of StickFight-DropIn\
#  into your Stick Fight folder (BepInEx + plugins + patched
#  Assembly-CSharp). Nothing of yours is deleted except what gets
#  replaced — and that is backed up first.
# ===================================================================
$ErrorActionPreference = 'Stop'
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Drop = Join-Path $Here 'StickFight-DropIn'

function Banner {
    Clear-Host
    Write-Host ''
    Write-Host '   sf-multiplayer   x   Stick Fight Oracle' -ForegroundColor Yellow
    Write-Host '   -----------------------------------------------------' -ForegroundColor DarkGray
    Write-Host '   1-Click Installer   |   kitslayer' -ForegroundColor DarkGray
    Write-Host ''
}

function Find-StickFight {
    $c = @((Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\StickFightTheGame'))
    try {
        $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath -replace '/','\'
        $c += (Join-Path $steam 'steamapps\common\StickFightTheGame')
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($line in Get-Content $vdf) {
                if ($line -match '"path"\s+"([^"]+)"') {
                    $c += (Join-Path ($matches[1] -replace '\\\\','\') 'steamapps\common\StickFightTheGame')
                }
            }
        }
    } catch { }
    foreach ($p in $c) { if ($p -and (Test-Path (Join-Path $p 'StickFight.exe'))) { return $p } }
    return $null
}

Banner
if (-not (Test-Path $Drop)) { throw "The StickFight-DropIn folder is missing next to this installer. Extract the WHOLE zip first - don't run the .bat from inside the zip preview." }

Write-Host '[1/4] Looking for Stick Fight...' -ForegroundColor Cyan
$Sf = Find-StickFight
if (-not $Sf) {
    Write-Host "      Couldn't find it automatically." -ForegroundColor Yellow
    $Sf = Read-Host '      Paste the path to your StickFightTheGame folder'
    if (-not (Test-Path (Join-Path $Sf 'StickFight.exe'))) { throw 'Invalid path (no StickFight.exe there).' }
}
Write-Host "      OK  $Sf" -ForegroundColor Green

# If the game is running, the DLL would be locked.
$proc = Get-Process StickFight -ErrorAction SilentlyContinue
if ($proc) { throw 'Stick Fight is RUNNING. Close it and run the installer again.' }

Write-Host '[2/4] Backing up your original Assembly-CSharp.dll...' -ForegroundColor Cyan
$asm = Join-Path $Sf 'StickFight_Data\Managed\Assembly-CSharp.dll'
$bak = "$asm.vanilla.bak"
if ((Test-Path $asm) -and -not (Test-Path $bak)) { Copy-Item $asm $bak -Force; Write-Host '      Saved: Assembly-CSharp.dll.vanilla.bak' -ForegroundColor Green }
elseif (Test-Path $bak) { Write-Host '      Backup already exists (leaving it alone).' -ForegroundColor Green }

if (Test-Path (Join-Path $Sf 'MelonLoader')) {
    Write-Host '      NOTE: you have MelonLoader. It coexists with BepInEx; if the online menu misbehaves, temporarily rename version.dll.' -ForegroundColor Yellow
}

Write-Host '[3/4] Copying the mod into Stick Fight...' -ForegroundColor Cyan
# Recursive merge-copy of the drop-in contents into the game folder.
Copy-Item (Join-Path $Drop '*') $Sf -Recurse -Force
Write-Host '      OK  BepInEx + plugins + patched Assembly-CSharp installed.' -ForegroundColor Green

Write-Host '[4/4] Launch options + desktop shortcut...' -ForegroundColor Cyan
$launchArgs = '-address 69.53.117.43 -port 1337'
$bat = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Play-StickFight.bat'
@"
@echo off
title sf-multiplayer  Stick Fight Oracle
start "" "$Sf\StickFight.exe" $launchArgs
"@ | Set-Content $bat -Encoding ASCII
Write-Host "      Shortcut: $bat" -ForegroundColor Green

Write-Host ''
Write-Host '===================================================================' -ForegroundColor Green
Write-Host '   INSTALL COMPLETE  -  sf-multiplayer' -ForegroundColor Yellow
Write-Host '===================================================================' -ForegroundColor Green
Write-Host ''
Write-Host '  Play:  double-click  Play-StickFight.bat  on your desktop' -ForegroundColor White
Write-Host '         (or in Steam -> Stick Fight -> Properties -> Launch' -ForegroundColor Gray
Write-Host "          Options:  $launchArgs )" -ForegroundColor Cyan
Write-Host '  In game: PLAY ONLINE -> QUICK MATCH. In the lobby type: /start' -ForegroundColor White
Write-Host ''
Write-Host '  Not working? Check BepInEx\LogOutput.log exists in the game' -ForegroundColor Gray
Write-Host '  folder after one launch - see README.txt, "TROUBLESHOOTING".' -ForegroundColor Gray
Write-Host ''
Write-Host '  Revert: UNINSTALL-sf-multiplayer.bat' -ForegroundColor DarkGray
Write-Host ''
