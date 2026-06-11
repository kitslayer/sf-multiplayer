# sf-multiplayer uninstaller — rolls Stick Fight back to EXACTLY how it was
# before the install, WITHOUT touching any other mods you had.
#
# It only:
#   - restores the Assembly-CSharp.dll we backed up at install time (this is YOUR
#     original file, modded or vanilla — we never overwrite that backup),
#   - removes ONLY the mod plugin DLLs (SFClientRecon / SFServerBrowser),
#   - restores doorstop_config.ini if we backed it up,
#   - removes the mod configs + desktop shortcut.
# Every other BepInEx plugin you have is left in place.
$ErrorActionPreference = 'Stop'

function Find-StickFight {
    $candidates = @((Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\StickFightTheGame'))
    try {
        $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath -replace '/', '\'
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($line in Get-Content $vdf) {
                if ($line -match '"path"\s+"([^"]+)"') {
                    $candidates += (Join-Path ($matches[1] -replace '\\\\','\') 'steamapps\common\StickFightTheGame')
                }
            }
        }
    } catch { }
    foreach ($c in $candidates) { if (Test-Path (Join-Path $c 'StickFight.exe')) { return $c } }
    return $null
}

Write-Host 'sf-multiplayer - Rolling Stick Fight back to its pre-install state...' -ForegroundColor Yellow
$Sf = Find-StickFight
if (-not $Sf) { $Sf = Read-Host 'Paste the path to your StickFightTheGame folder' }

$Managed    = Join-Path $Sf 'StickFight_Data\Managed'
$TargetAsm  = Join-Path $Managed 'Assembly-CSharp.dll'
$VanillaBak = Join-Path $Managed 'Assembly-CSharp.dll.vanilla.bak'
$Plug       = Join-Path $Sf 'BepInEx\plugins'

# 1) Restore the original Assembly-CSharp.dll (your pre-install file).
if (Test-Path $VanillaBak) {
    Copy-Item $VanillaBak $TargetAsm -Force
    Write-Host 'OK  Restored your original Assembly-CSharp.dll.' -ForegroundColor Green
} else {
    Write-Host '!!  No backup found. If the game misbehaves, Steam > Verify integrity of game files.' -ForegroundColor Yellow
}

# 2) Remove ONLY the mod plugins. Anything else in /plugins is left alone.
foreach ($p in @('SFClientRecon.dll','SFServerBrowser.dll')) {
    $f = Join-Path $Plug $p
    if (Test-Path $f) { Remove-Item $f -Force; Write-Host ("OK  Removed mod plugin {0}" -f $p) -ForegroundColor Green }
}

# 3) Restore doorstop if we backed it up.
$doorstop = Join-Path $Sf 'doorstop_config.ini'
if (Test-Path "$doorstop.alka.bak") {
    Copy-Item "$doorstop.alka.bak" $doorstop -Force
    Write-Host 'OK  Restored doorstop_config.ini.' -ForegroundColor Green
}

# 4) Remove the mod's config files (oracle endpoint + language choice).
foreach ($cfg in @('BepInEx\config\sf-oracle-endpoint.txt','BepInEx\config\alka-lang.txt')) {
    $cf = Join-Path $Sf $cfg
    if (Test-Path $cf) { Remove-Item $cf -Force }
}

# 5) Remove the desktop shortcut (current name + the old Spanish one from earlier installs).
foreach ($name in @('Play-StickFight.bat','Jugar-StickFight.bat')) {
    $b = Join-Path ([Environment]::GetFolderPath('Desktop')) $name
    if (Test-Path $b) { Remove-Item $b -Force }
}

Write-Host ''
Write-Host 'Done. Stick Fight is back to how it was before the mod — your other mods are untouched.' -ForegroundColor Green
Write-Host 'If you added "-address ..." Steam launch options for the mod, remove them too.' -ForegroundColor Gray
