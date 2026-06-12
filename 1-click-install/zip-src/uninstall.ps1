# ===================================================================
#  sf-multiplayer  -  Uninstall / revert to vanilla Stick Fight
# ===================================================================
$ErrorActionPreference = 'Stop'

function Find-StickFight {
    $c = @((Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\StickFightTheGame'))
    try {
        $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath -replace '/','\'
        $c += (Join-Path $steam 'steamapps\common\StickFightTheGame')
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($line in Get-Content $vdf) {
                if ($line -match '"path"\s+"([^"]+)"') { $c += (Join-Path ($matches[1] -replace '\\\\','\') 'steamapps\common\StickFightTheGame') }
            }
        }
    } catch { }
    foreach ($p in $c) { if ($p -and (Test-Path (Join-Path $p 'StickFight.exe'))) { return $p } }
    return $null
}

Write-Host 'sf-multiplayer - Reverting to vanilla Stick Fight...' -ForegroundColor Yellow
$Sf = Find-StickFight
if (-not $Sf) { $Sf = Read-Host 'Paste the path to your StickFightTheGame folder' }
if (Get-Process StickFight -ErrorAction SilentlyContinue) { throw 'Close Stick Fight first.' }

$asm = Join-Path $Sf 'StickFight_Data\Managed\Assembly-CSharp.dll'
$bak = "$asm.vanilla.bak"
if (Test-Path $bak) { Copy-Item $bak $asm -Force; Remove-Item $bak -Force; Write-Host 'OK  Vanilla Assembly-CSharp.dll restored.' -ForegroundColor Green }
else { Write-Host '!!  No backup found. Use Steam -> Stick Fight -> Properties -> Installed Files -> Verify integrity to restore it.' -ForegroundColor Yellow }

# Remove our plugins
foreach ($p in @('SFClientRecon.dll','SFServerBrowser.dll')) {
    $f = Join-Path $Sf "BepInEx\plugins\$p"
    if (Test-Path $f) { Remove-Item $f -Force; Write-Host "OK  Removed BepInEx\plugins\$p" -ForegroundColor Green }
}

# Turn BepInEx off (files stay, nothing loads) so the game is 100% vanilla.
$ds = Join-Path $Sf 'doorstop_config.ini'
if (Test-Path $ds) { (Get-Content $ds -Raw) -replace 'enabled\s*=\s*true','enabled = false' | Set-Content $ds -NoNewline; Write-Host 'OK  BepInEx disabled (doorstop enabled=false).' -ForegroundColor Green }

# Remove the desktop shortcut (both the current name and the old Spanish one).
foreach ($name in @('Play-StickFight.bat','Jugar-StickFight.bat')) {
    $bat = Join-Path ([Environment]::GetFolderPath('Desktop')) $name
    if (Test-Path $bat) { Remove-Item $bat -Force }
}

Write-Host ''
Write-Host 'Done. Stick Fight is back to normal.' -ForegroundColor Green
Write-Host 'If you added -address to your Steam Launch Options, remove it.' -ForegroundColor Gray
