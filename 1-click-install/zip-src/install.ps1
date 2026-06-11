# ===================================================================
#  sf-multiplayer  -  Stick Fight Oracle  -  Instalador 1-Click
#  kitslayer
#
#  Detecta Stick Fight en cualquier biblioteca de Steam, hace BACKUP de tu
#  Assembly-CSharp.dll original y COPIA el contenido de StickFight-DropIn\
#  dentro de tu carpeta de Stick Fight (BepInEx + plugins + Assembly-CSharp
#  parcheado). No borra nada tuyo salvo lo que reemplaza (con backup).
# ===================================================================
$ErrorActionPreference = 'Stop'
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Drop = Join-Path $Here 'StickFight-DropIn'

function Banner {
    Clear-Host
    Write-Host ''
    Write-Host '   sf-multiplayer   x   Stick Fight Oracle' -ForegroundColor Yellow
    Write-Host '   -----------------------------------------------------' -ForegroundColor DarkGray
    Write-Host '   Instalador 1-Click   |   kitslayer' -ForegroundColor DarkGray
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
if (-not (Test-Path $Drop)) { throw "Falta la carpeta StickFight-DropIn junto a este instalador." }

Write-Host '[1/4] Buscando Stick Fight...' -ForegroundColor Cyan
$Sf = Find-StickFight
if (-not $Sf) {
    Write-Host '      No lo encontre automaticamente.' -ForegroundColor Yellow
    $Sf = Read-Host '      Pega la ruta de la carpeta StickFightTheGame'
    if (-not (Test-Path (Join-Path $Sf 'StickFight.exe'))) { throw 'Ruta invalida (no hay StickFight.exe).' }
}
Write-Host "      OK  $Sf" -ForegroundColor Green

# Si esta corriendo, avisar (el DLL estaria bloqueado)
$proc = Get-Process StickFight -ErrorAction SilentlyContinue
if ($proc) { throw 'Stick Fight esta ABIERTO. Cerralo y volve a ejecutar el instalador.' }

Write-Host '[2/4] Backup de tu Assembly-CSharp.dll original...' -ForegroundColor Cyan
$asm = Join-Path $Sf 'StickFight_Data\Managed\Assembly-CSharp.dll'
$bak = "$asm.vanilla.bak"
if ((Test-Path $asm) -and -not (Test-Path $bak)) { Copy-Item $asm $bak -Force; Write-Host '      Guardado: Assembly-CSharp.dll.vanilla.bak' -ForegroundColor Green }
elseif (Test-Path $bak) { Write-Host '      Backup ya existe (no lo piso).' -ForegroundColor Green }

if (Test-Path (Join-Path $Sf 'MelonLoader')) {
    Write-Host '      AVISO: tenes MelonLoader. Convive con BepInEx; si el menu online falla, renombra version.dll temporalmente.' -ForegroundColor Yellow
}

Write-Host '[3/4] Copiando el mod dentro de Stick Fight...' -ForegroundColor Cyan
# Copia recursiva del contenido del drop-in dentro de la carpeta del juego (merge).
Copy-Item (Join-Path $Drop '*') $Sf -Recurse -Force
Write-Host '      OK  BepInEx + plugins + Assembly-CSharp parcheado instalados.' -ForegroundColor Green

Write-Host '[4/4] Opciones de lanzamiento + acceso directo...' -ForegroundColor Cyan
$launchArgs = '-address 69.53.117.43 -port 1337'
$bat = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Jugar-StickFight.bat'
@"
@echo off
title sf-multiplayer  Stick Fight Oracle
start "" "$Sf\StickFight.exe" $launchArgs
"@ | Set-Content $bat -Encoding ASCII
Write-Host "      Acceso directo: $bat" -ForegroundColor Green

Write-Host ''
Write-Host '===================================================================' -ForegroundColor Green
Write-Host '   INSTALACION COMPLETA  -  sf-multiplayer' -ForegroundColor Yellow
Write-Host '===================================================================' -ForegroundColor Green
Write-Host ''
Write-Host '  Jugar:  doble clic en el escritorio  Jugar-StickFight.bat' -ForegroundColor White
Write-Host '          (o en Steam -> Stick Fight -> Propiedades -> Opciones de' -ForegroundColor Gray
Write-Host "           lanzamiento:  $launchArgs )" -ForegroundColor Cyan
Write-Host '  En el juego: PLAY ONLINE -> QUICK MATCH. En el lobby: /start' -ForegroundColor White
Write-Host ''
Write-Host '  Revertir: DESINSTALAR-sf-multiplayer.bat' -ForegroundColor DarkGray
Write-Host ''
