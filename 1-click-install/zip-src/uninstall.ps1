# ===================================================================
#  sf-multiplayer  -  Desinstalar / revertir a Stick Fight vanilla
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

Write-Host 'sf-multiplayer - Revirtiendo a Stick Fight vanilla...' -ForegroundColor Yellow
$Sf = Find-StickFight
if (-not $Sf) { $Sf = Read-Host 'Pega la ruta de StickFightTheGame' }
if (Get-Process StickFight -ErrorAction SilentlyContinue) { throw 'Cerra Stick Fight primero.' }

$asm = Join-Path $Sf 'StickFight_Data\Managed\Assembly-CSharp.dll'
$bak = "$asm.vanilla.bak"
if (Test-Path $bak) { Copy-Item $bak $asm -Force; Remove-Item $bak -Force; Write-Host 'OK  Assembly-CSharp.dll vanilla restaurado.' -ForegroundColor Green }
else { Write-Host '!!  Sin backup. Steam -> Stick Fight -> Propiedades -> Archivos -> Verificar integridad para restaurar.' -ForegroundColor Yellow }

# Quitar nuestros plugins
foreach ($p in @('SFClientRecon.dll','SFServerBrowser.dll')) {
    $f = Join-Path $Sf "BepInEx\plugins\$p"
    if (Test-Path $f) { Remove-Item $f -Force; Write-Host "OK  Quitado BepInEx\plugins\$p" -ForegroundColor Green }
}

# Apagar BepInEx (deja los archivos pero no se carga) para volver 100% vanilla.
$ds = Join-Path $Sf 'doorstop_config.ini'
if (Test-Path $ds) { (Get-Content $ds -Raw) -replace 'enabled\s*=\s*true','enabled = false' | Set-Content $ds -NoNewline; Write-Host 'OK  BepInEx desactivado (doorstop enabled=false).' -ForegroundColor Green }

$bat = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Jugar-StickFight.bat'
if (Test-Path $bat) { Remove-Item $bat -Force }

Write-Host ''
Write-Host 'Listo. Stick Fight vuelve a su estado normal.' -ForegroundColor Green
Write-Host 'Si pusiste -address en las Opciones de lanzamiento de Steam, quitalas.' -ForegroundColor Gray
