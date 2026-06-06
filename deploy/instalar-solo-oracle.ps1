# Cliente oracle SOLO: BepInEx + srv.dll + SFClientRecon. Sin MelonLoader / MLLoader.
# Team: kitslayer + AlkaDev
param(
    [string]$ServerIp = '69.53.117.43',
    [string]$ServerPort = '1337'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'team-sf-multiplayer.ps1')
Show-SfMultiplayerTeamInfo
$RepoRoot = Split-Path $PSScriptRoot -Parent
$Sf = Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\StickFightTheGame'

if (-not (Test-Path (Join-Path $Sf 'StickFight.exe'))) {
    throw "No encuentro Stick Fight en $Sf"
}

Write-Host '=== Oracle solo (sin MelonLoader) ===' -ForegroundColor Cyan

# BepInEx + winhttp si falta (instalacion limpia de Steam)
& (Join-Path $PSScriptRoot 'fix-oracle-launch.ps1')

# Melon / MLLoader fuera del camino (no borrar, solo renombrar)
foreach ($dir in @('MelonLoader', 'MLLoader', 'Mods', 'Plugins')) {
    $path = Join-Path $Sf $dir
    $off = $path + '.oracle-off'
    if ((Test-Path $path) -and -not (Test-Path $off)) {
        Rename-Item $path $off
        Write-Host "  Desactivado: $dir -> $($dir).oracle-off"
    }
}
# Proxy de Melon (version.dll) compite con BepInEx
$ver = Join-Path $Sf 'version.dll'
if (Test-Path $ver) {
    $bak = Join-Path $Sf 'version.dll.pre-oracle.bak'
    if (-not (Test-Path $bak)) { Copy-Item $ver $bak -Force }
    Remove-Item $ver -Force -ErrorAction SilentlyContinue
    Write-Host '  Quitado version.dll (proxy Melon) — BepInEx usa winhttp.dll'
}

& (Join-Path $PSScriptRoot 'instalar-cliente-oracle.ps1') -ServerIp $ServerIp -ServerPort $ServerPort

Write-Host ''
Write-Host '=== Listo: solo oracle ===' -ForegroundColor Green
Write-Host "Steam launch: -address $ServerIp -port $ServerPort"
Write-Host 'Menu: QUICK MATCH o HOST MATCH | En mapa: /start'
Write-Host 'Para recuperar Melon: renombra *.oracle-off a su nombre original'
