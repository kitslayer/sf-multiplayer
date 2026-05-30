# Restaura Stick Fight a vanilla (revierte el instalador ALKA-KITSLAYER).
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

Write-Host 'ALKA-KITSLAYER - Restaurando Stick Fight vanilla...' -ForegroundColor Yellow
$Sf = Find-StickFight
if (-not $Sf) { $Sf = Read-Host 'Pega la ruta de StickFightTheGame' }

$Managed    = Join-Path $Sf 'StickFight_Data\Managed'
$TargetAsm  = Join-Path $Managed 'Assembly-CSharp.dll'
$VanillaBak = Join-Path $Managed 'Assembly-CSharp.dll.vanilla.bak'
$Plug       = Join-Path $Sf 'BepInEx\plugins'

if (Test-Path $VanillaBak) {
    Copy-Item $VanillaBak $TargetAsm -Force
    Write-Host 'OK  Assembly-CSharp.dll vanilla restaurado.' -ForegroundColor Green
} else {
    Write-Host '!!  No hay backup vanilla; verifica integridad desde Steam para restaurar.' -ForegroundColor Yellow
}

foreach ($p in @('SFClientRecon.dll','SFServerBrowser.dll')) {
    $f = Join-Path $Plug $p
    if (Test-Path $f) { Remove-Item $f -Force; Write-Host ("OK  Quitado {0}" -f $p) -ForegroundColor Green }
}

# Restaurar doorstop si lo respaldamos
$doorstop = Join-Path $Sf 'doorstop_config.ini'
if (Test-Path "$doorstop.alka.bak") { Copy-Item "$doorstop.alka.bak" $doorstop -Force }

# Quitar acceso directo
$bat = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Jugar-StickFight-ALKA.bat'
if (Test-Path $bat) { Remove-Item $bat -Force }

Write-Host ''
Write-Host 'Listo. Stick Fight vuelve a su estado normal.' -ForegroundColor Green
Write-Host 'Quita tambien las opciones de lanzamiento -address en Steam si las pusiste.' -ForegroundColor Gray
