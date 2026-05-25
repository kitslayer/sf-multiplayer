# Oracle + MelonLoader (skins) en la misma sesion Steam.
# Problema: Melon usa version.dll y BepInEx usa winhttp.dll — solo uno gana al abrir el .exe.
# Solucion: BepInEx arranca primero + BepInEx.MelonLoader.Loader carga tus Mods de Melon.
param(
    [string]$ServerIp = '69.53.117.43',
    [string]$ServerPort = '1337'
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
$Sf = Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\StickFightTheGame'
$MlZip = Join-Path $PSScriptRoot 'MLLoader-UnityMono-BepInEx5-v0.5.7.zip'
$MlUrl = 'https://github.com/BepInEx/BepInEx.MelonLoader.Loader/releases/download/v2.1.0/MLLoader-UnityMono-BepInEx5-v0.5.7.zip'

Set-Location $Sf
if (-not (Test-Path 'StickFight.exe')) { throw "No encuentro Stick Fight en $Sf" }
if (Get-Process -Name 'StickFight' -ErrorAction SilentlyContinue) {
    throw 'Cierra Stick Fight antes de instalar (version.dll esta en uso).'
}

Write-Host '=== [1/5] Cliente oracle (srv + BepInEx + SFClientRecon) ===' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'instalar-cliente-oracle.ps1') -ServerIp $ServerIp -ServerPort $ServerPort

Write-Host '=== [2/5] BepInEx.MelonLoader.Loader (Melon 0.5.7 bajo BepInEx) ===' -ForegroundColor Cyan
if (-not (Test-Path $MlZip)) {
    Write-Host "Descargando MLLoader..."
    Invoke-WebRequest -Uri $MlUrl -OutFile $MlZip -UseBasicParsing
}
Expand-Archive -Path $MlZip -DestinationPath $Sf -Force

Write-Host '=== [3/5] Proxy: BepInEx gana sobre Melon (version.dll) ===' -ForegroundColor Cyan
$melonProxy = Join-Path $Sf 'version.dll'
$bepProxy = Join-Path $Sf 'winhttp.dll'
$melonBak = Join-Path $Sf 'version.dll.melon.orig'
if (-not (Test-Path $bepProxy)) { throw 'Falta winhttp.dll de BepInEx. Ejecuta fix-oracle-launch.ps1' }
if (-not (Test-Path $melonBak) -and (Test-Path $melonProxy)) {
    Copy-Item $melonProxy $melonBak -Force
    Write-Host "  Backup Melon proxy -> version.dll.melon.orig"
}
Copy-Item $bepProxy $melonProxy -Force
Write-Host '  version.dll = copia de winhttp.dll (BepInEx + MLLoader arrancan; Melon directo NO)'

Write-Host '=== [4/5] Mods Melon -> MLLoader\Mods ===' -ForegroundColor Cyan
# MLLoader oficial: MLLoader\Mods y MLLoader\Plugins (crear si el zip no los trae)
$mlMods = Join-Path $Sf 'MLLoader\Mods'
$mlPlugins = Join-Path $Sf 'MLLoader\Plugins'
New-Item -ItemType Directory -Path $mlMods -Force | Out-Null
New-Item -ItemType Directory -Path $mlPlugins -Force | Out-Null
$legacyMods = Join-Path $Sf 'Mods'
$legacyPlugins = Join-Path $Sf 'Plugins'
foreach ($pair in @(
        @($legacyMods, $mlMods, 'Mods'),
        @($legacyPlugins, $mlPlugins, 'Plugins')
    )) {
    $src, $dst, $label = $pair
    if (-not (Test-Path $src)) { continue }
    Get-ChildItem $src -Filter '*.dll' -ErrorAction SilentlyContinue | ForEach-Object {
        $dest = Join-Path $dst $_.Name
        Copy-Item $_.FullName $dest -Force
        $rel = $dest.Substring($Sf.Length).TrimStart('\')
        Write-Host "  Copiado $($_.Name) -> $rel"
    }
}

Write-Host '=== [5/5] Doorstop BepInEx ON ===' -ForegroundColor Cyan
$iniPath = Join-Path $Sf 'doorstop_config.ini'
$ini = Get-Content $iniPath -Raw
$ini = $ini -replace 'enabled = false', 'enabled = true'
if ($ini -notmatch 'enabled = true') { $ini = $ini -replace 'enabled\s*=\s*\w+', 'enabled = true' }
Set-Content $iniPath $ini -NoNewline

Write-Host ''
Write-Host '=== LISTO: Steam + skins Melon + Oracle ===' -ForegroundColor Green
Write-Host "Launch options Steam:"
Write-Host "  -address $ServerIp -port $ServerPort"
Write-Host ''
Write-Host 'Al abrir el juego deberias ver en MelonLoader\Logs Y en BepInEx\LogOutput.log'
Write-Host '  BepInEx: SFClientRecon 0.3.14 + [oracle-lobby]'
Write-Host '  Melon: AlkaRealSkinChanger (desde MLLoader)'
Write-Host ''
Write-Host 'Si solo ves log de Melon sin BepInEx, NO ejecutes restaurar-juego-normal.ps1'
Write-Host 'Vuelve a correr este script.'
Write-Host ''
Write-Host 'Volver solo Melon sin oracle: deploy\restaurar-juego-normal.ps1'
