# Instalacion completa oracle en Stick Fight LIMPIO (o reinstalado).
# Instala BepInEx 5.4.23.5 + Assembly-CSharp.srv + SFClientRecon 0.3.4. Sin MelonLoader.
param(
    [string]$ServerIp = '69.53.117.43',
    [string]$ServerPort = '1337',
    [switch]$ForceBepInEx
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
$Sf = Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\StickFightTheGame'
$SrvDll = Join-Path $RepoRoot 'client-mod\dll\Assembly-CSharp.srv.v25.dll'
$Managed = Join-Path $Sf 'StickFight_Data\Managed'
$Asm = Join-Path $Managed 'Assembly-CSharp.dll'
$VanillaBak = Join-Path $Managed 'Assembly-CSharp.dll.vanilla.bak'
$Plug = Join-Path $Sf 'BepInEx\plugins'
$BepZip = Join-Path $env:TEMP 'BepInEx_win_x86_5.4.23.5.zip'
$BepUri = 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x86_5.4.23.5.zip'

if (-not (Test-Path (Join-Path $Sf 'StickFight.exe'))) {
    throw "No encuentro Stick Fight. Ruta esperada: $Sf"
}
if (-not (Test-Path $SrvDll)) {
    throw "Falta $SrvDll en el repo"
}

Write-Host ''
Write-Host '========================================' -ForegroundColor Cyan
Write-Host '  INSTALACION ORACLE DESDE CERO' -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan
Write-Host ''

# --- 1) BepInEx ---
$TmpExtract = Join-Path $env:TEMP 'BepInExExtract'
$needBep = $ForceBepInEx -or -not (Test-Path (Join-Path $Sf 'BepInEx\core\BepInEx.dll'))
if ($needBep -or -not (Test-Path (Join-Path $Sf 'doorstop_config.ini'))) {
    Write-Host '[1/5] Descargando e instalando BepInEx 5.4.23.5...' -ForegroundColor Yellow
    if (-not (Test-Path $BepZip)) { Invoke-WebRequest -Uri $BepUri -OutFile $BepZip -UseBasicParsing }
    if (Test-Path $TmpExtract) { Remove-Item $TmpExtract -Recurse -Force }
    New-Item -ItemType Directory -Path $TmpExtract -Force | Out-Null
    Expand-Archive -Path $BepZip -DestinationPath $TmpExtract -Force
    Copy-Item (Join-Path $TmpExtract 'doorstop_config.ini') $Sf -Force
    Copy-Item (Join-Path $TmpExtract '.doorstop_version') $Sf -Force -ErrorAction SilentlyContinue
    robocopy (Join-Path $TmpExtract 'BepInEx') (Join-Path $Sf 'BepInEx') /E /XO /NFL /NDL /NJH /NJS | Out-Null
    if (-not (Test-Path (Join-Path $Sf 'winhttp.dll'))) {
        Copy-Item (Join-Path $TmpExtract 'winhttp.dll') (Join-Path $Sf 'winhttp.dll') -Force
    }
    Write-Host '      BepInEx instalado (sin pisar winhttp si esta en uso).'
} else {
    Write-Host '[1/5] BepInEx ya presente — OK.'
}

# --- 2) Doorstop ON ---
Write-Host '[2/5] Activando doorstop (BepInEx al arrancar)...' -ForegroundColor Yellow
$iniPath = Join-Path $Sf 'doorstop_config.ini'
if (-not (Test-Path $iniPath)) {
    throw 'Falta doorstop_config.ini tras BepInEx — reinstala BepInEx.'
}
$ini = Get-Content $iniPath -Raw
$ini = $ini -replace 'enabled = false', 'enabled = true'
if ($ini -notmatch 'enabled = true') { $ini = $ini -replace 'enabled\s*=\s*\w+', 'enabled = true' }
Set-Content $iniPath $ini -NoNewline

# Quitar proxy Melon si existe
$ver = Join-Path $Sf 'version.dll'
if (Test-Path $ver) {
    if (-not (Test-Path ($Sf + '\version.dll.pre-oracle.bak'))) { Copy-Item $ver ($Sf + '\version.dll.pre-oracle.bak') -Force }
    Remove-Item $ver -Force
    Write-Host '      version.dll (Melon) quitado.'
}
foreach ($dir in @('MelonLoader', 'MLLoader', 'Mods', 'Plugins')) {
    $p = Join-Path $Sf $dir
    $off = $p + '.oracle-off'
    if ((Test-Path $p) -and -not (Test-Path $off)) { Rename-Item $p $off; Write-Host "      $dir desactivado." }
}

# --- 3) Compilar SFClientRecon ---
Write-Host '[3/5] Compilando SFClientRecon + copiando plugin...' -ForegroundColor Yellow
$ClientDll = Join-Path $RepoRoot 'sf-client-recon\bin\Release\SFClientRecon.dll'
& (Join-Path $RepoRoot 'deploy-physics-fix.ps1') -InstallLocal
$dest = Join-Path $Plug 'SFClientRecon.dll'
try {
    Copy-Item $ClientDll $dest -Force
} catch {
    Copy-Item $ClientDll (Join-Path $Plug 'SFClientRecon.new.dll') -Force
    Write-Host '      AVISO: Cierra Stick Fight y vuelve a ejecutar este script, o usa el .bat del escritorio.'
}
if (-not (Test-Path $dest) -and -not (Test-Path (Join-Path $Plug 'SFClientRecon.new.dll'))) {
    throw 'No se pudo copiar SFClientRecon.dll'
}
# Sin MelonLoader dentro de BepInEx
$mlPlug = Join-Path $Plug 'BepInEx.MelonLoader.Loader'
if (Test-Path $mlPlug) { Remove-Item $mlPlug -Recurse -Force -ErrorAction SilentlyContinue }
$hostDll = Join-Path $Plug 'SFHeadlessHost.dll'
if (Test-Path $hostDll) {
    Move-Item $hostDll (Join-Path $Plug 'SFHeadlessHost.dll.oracle-client-off') -Force -ErrorAction SilentlyContinue
}

# --- 4) Assembly-CSharp.srv ---
Write-Host '[4/5] Instalando mod UDP (Assembly-CSharp.srv.v25)...' -ForegroundColor Yellow
if (-not (Test-Path $VanillaBak)) {
    Copy-Item $Asm $VanillaBak -Force
    Write-Host '      Backup vanilla -> Assembly-CSharp.dll.vanilla.bak'
}
Copy-Item $SrvDll $Asm -Force
if ((Get-FileHash $Asm).Hash -ne (Get-FileHash $SrvDll).Hash) {
    throw 'Assembly-CSharp.dll no coincide con srv.v25'
}
Write-Host '      srv.v25 activo.'

# --- 5) Acceso directo (ruta absoluta al .ps1 del repo; no copiar solo el .bat) ---
Write-Host '[5/5] Acceso directo en el escritorio...' -ForegroundColor Yellow
$desk = [Environment]::GetFolderPath('Desktop')
$oraclePs1 = Join-Path $PSScriptRoot 'jugar-oracle.ps1'
$deskBat = Join-Path $desk 'Jugar Stick Fight Oracle.bat'
@"
@echo off
title Stick Fight Oracle
powershell -NoProfile -ExecutionPolicy Bypass -File "$oraclePs1"
pause
"@ | Set-Content -Path $deskBat -Encoding ASCII

Write-Host ''
Write-Host '========================================' -ForegroundColor Green
Write-Host '  LISTO — Oracle instalado' -ForegroundColor Green
Write-Host '========================================' -ForegroundColor Green
Write-Host ''
Write-Host 'Pon en Steam -> Propiedades -> Opciones de lanzamiento:' -ForegroundColor White
Write-Host "  -address $ServerIp -port $ServerPort" -ForegroundColor Yellow
Write-Host ''
Write-Host 'Abre el juego (Steam o el .bat del escritorio).' -ForegroundColor White
Write-Host 'Menu: QUICK MATCH o HOST MATCH' -ForegroundColor White
Write-Host 'En el mapa: escribe /start en el chat' -ForegroundColor White
Write-Host ''
Write-Host "Plugin: $Plug\SFClientRecon.dll" -ForegroundColor DarkGray
Write-Host "Log:    $Sf\BepInEx\LogOutput.log" -ForegroundColor DarkGray
