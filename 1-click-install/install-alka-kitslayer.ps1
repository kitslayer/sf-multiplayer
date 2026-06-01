# ===================================================================
#  ALKA-KITSLAYER  -  Stick Fight: The Game  -  Oracle 1-Click Installer
#  Team: kitslayer + AlkaDev  |  GitHub: kitslayer, AlkaPrime12
#
#  Qué hace (sin romper nada de tu carpeta):
#   1. Encuentra Stick Fight en cualquier biblioteca de Steam.
#   2. Hace BACKUP de tu Assembly-CSharp.dll y doorstop_config.ini.
#   3. Detecta tu loader: BepInEx y/o MelonLoader, y se adapta solo.
#   4. Instala BepInEx (si falta), el Assembly-CSharp parcheado y el plugin
#      SFClientRecon (+ SFServerBrowser).
#   5. Deja las opciones de lanzamiento listas (-address del oracle).
#   6. Crea "Jugar-StickFight-ALKA.bat" en el escritorio.
#
#  Para revertir: corre DESINSTALAR-ALKA-KITSLAYER.bat (restaura el backup).
# ===================================================================

param(
    [string]$ServerIp   = '69.53.117.43',
    [string]$ServerPort = '1337'
)

$ErrorActionPreference = 'Stop'
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
# El repo: assets prebuildeados en ..\dist y ..\client-mod\dll
$RepoRoot = Split-Path $Here -Parent

# ---------------------------------------------------------------- banner
function Show-AlkaKitslayerBanner {
    Clear-Host
    $c = 'Cyan'; $y = 'Yellow'; $m = 'Magenta'
    Write-Host ''
    Write-Host '   ___    _    _  __    _         _  _____ _____ ____  _      _____   ___ _____ ____  ' -ForegroundColor $c
    Write-Host '  / _ \  | |  | |/ /   / \       | |/ /_ _|_   _/ ___|| |    / \ \ \ / / __|  _ \  ' -ForegroundColor $c
    Write-Host ' | |_| | | |  | | /   / _ \  ___ | | < | |  | | \___ \| |   / _ \ V / |__| |_) | ' -ForegroundColor $m
    Write-Host ' |  _  | | |__| |\ \  / ___ \|___|| |\ \| |  | |  ___) | |__/ ___ \ | |  _| _ <  ' -ForegroundColor $m
    Write-Host ' |_| |_| |____|_| \_\/_/   \_\   |_| \_\___| |_| |____/|____/_/   \_\_|_| |_| \_\ ' -ForegroundColor $y
    Write-Host ''
    Write-Host '        A L K A - K I T S L A Y E R   x   Stick Fight Oracle' -ForegroundColor $y
    Write-Host '        ------------------------------------------------------' -ForegroundColor DarkGray
    Write-Host '        Team: kitslayer + AlkaDev    GitHub: kitslayer / AlkaPrime12' -ForegroundColor DarkGray
    Write-Host ''
    Start-Sleep -Milliseconds 600
}

function Step($n, $txt) { Write-Host ("[{0}] {1}" -f $n, $txt) -ForegroundColor Cyan }
function Ok($txt)       { Write-Host ("    OK  {0}" -f $txt) -ForegroundColor Green }
function Warn($txt)     { Write-Host ("    !!  {0}" -f $txt) -ForegroundColor Yellow }

# ----------------------------------------------------- detectar Stick Fight
function Find-StickFight {
    $candidates = @()
    # 1) Ruta default
    $candidates += (Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\StickFightTheGame')
    # 2) Steam install desde el registro -> libraryfolders.vdf
    try {
        $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath
        if ($steam) {
            $steam = $steam -replace '/', '\'
            $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
            if (Test-Path $vdf) {
                foreach ($line in Get-Content $vdf) {
                    if ($line -match '"path"\s+"([^"]+)"') {
                        $lib = $matches[1] -replace '\\\\', '\'
                        $candidates += (Join-Path $lib 'steamapps\common\StickFightTheGame')
                    }
                }
            }
            $candidates += (Join-Path $steam 'steamapps\common\StickFightTheGame')
        }
    } catch { }
    foreach ($c in $candidates) {
        if ($c -and (Test-Path (Join-Path $c 'StickFight.exe'))) { return $c }
    }
    return $null
}

# ============================================================ MAIN
Show-AlkaKitslayerBanner

Step 1 'Buscando Stick Fight: The Game...'
$Sf = Find-StickFight
if (-not $Sf) {
    Write-Host ''
    Warn 'No encontre Stick Fight automaticamente.'
    $Sf = Read-Host '    Pega la ruta de la carpeta StickFightTheGame'
    if (-not (Test-Path (Join-Path $Sf 'StickFight.exe'))) { throw 'Ruta invalida: no hay StickFight.exe ahi.' }
}
Ok $Sf

$Managed   = Join-Path $Sf 'StickFight_Data\Managed'
$TargetAsm = Join-Path $Managed 'Assembly-CSharp.dll'
$VanillaBak= Join-Path $Managed 'Assembly-CSharp.dll.vanilla.bak'
$Plug      = Join-Path $Sf 'BepInEx\plugins'

# Assets prebuildeados. Primero buscamos en la carpeta local 'files\'
# (paquete autocontenido); si no, caemos al repo (dist\ / client-mod\).
$FilesDir = Join-Path $Here 'files'
function Resolve-Asset($name, $repoRel) {
    $local = Join-Path $FilesDir $name
    if (Test-Path $local) { return $local }
    return (Join-Path $RepoRoot $repoRel)
}
$SrvDll   = Resolve-Asset 'Assembly-CSharp.srv.v25.dll' 'client-mod\dll\Assembly-CSharp.srv.v25.dll'
$ReconDll = Resolve-Asset 'SFClientRecon.dll'           'dist\SFClientRecon.dll'
$BrowDll  = Resolve-Asset 'SFServerBrowser.dll'         'dist\SFServerBrowser.dll'
foreach ($f in @($SrvDll, $ReconDll)) {
    if (-not (Test-Path $f)) { throw ("Falta un archivo de la descarga: {0}" -f $f) }
}

# ----------------------------------------------------- detectar loaders
Step 2 'Detectando loader (BepInEx / MelonLoader)...'
$hasBepInEx = (Test-Path (Join-Path $Sf 'BepInEx')) -or (Test-Path (Join-Path $Sf 'winhttp.dll'))
$hasMelon   = (Test-Path (Join-Path $Sf 'MelonLoader')) -or (Test-Path (Join-Path $Sf 'version.dll'))
if ($hasMelon) { Warn 'MelonLoader detectado.' } else { Ok 'Sin MelonLoader.' }
if ($hasBepInEx) { Ok 'BepInEx ya presente.' } else { Warn 'BepInEx no esta: lo instalo.' }

# ----------------------------------------------------- instalar BepInEx si falta
if (-not $hasBepInEx) {
    Step '2b' 'Descargando BepInEx 5.4.23.2 (x86)...'
    $bepUrl = 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x86_5.4.23.2.zip'
    $tmpZip = Join-Path $env:TEMP 'BepInEx_alka.zip'
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $bepUrl -OutFile $tmpZip -UseBasicParsing
        Expand-Archive -Path $tmpZip -DestinationPath $Sf -Force
        Remove-Item $tmpZip -Force
        Ok 'BepInEx instalado.'
    } catch {
        throw ("No pude descargar BepInEx automaticamente: {0}. Instalalo manual y reintenta." -f $_.Exception.Message)
    }
}

# ----------------------------------------------------- backup (no romper nada)
Step 3 'Backup de tus archivos originales...'
if (-not (Test-Path $VanillaBak)) {
    if (Test-Path $TargetAsm) { Copy-Item $TargetAsm $VanillaBak -Force; Ok 'Assembly-CSharp.dll respaldado (.vanilla.bak).' }
} else { Ok 'Backup vanilla ya existe (no lo piso).' }
$doorstop = Join-Path $Sf 'doorstop_config.ini'
if ((Test-Path $doorstop) -and -not (Test-Path "$doorstop.alka.bak")) {
    Copy-Item $doorstop "$doorstop.alka.bak" -Force
}

# ----------------------------------------------------- instalar parche + plugins
Step 4 'Instalando Assembly-CSharp parcheado (lobby -> oracle)...'
Copy-Item $SrvDll $TargetAsm -Force
Ok 'Assembly-CSharp.srv.v25 activo.'

Step 5 'Instalando plugins en BepInEx...'
if (-not (Test-Path $Plug)) { New-Item -ItemType Directory -Path $Plug -Force | Out-Null }
Copy-Item $ReconDll (Join-Path $Plug 'SFClientRecon.dll') -Force
Ok 'SFClientRecon.dll'
if (Test-Path $BrowDll) { Copy-Item $BrowDll (Join-Path $Plug 'SFServerBrowser.dll') -Force; Ok 'SFServerBrowser.dll' }
# El host NUNCA va en la PC del jugador
$hostDll = Join-Path $Plug 'SFHeadlessHost.dll'
if (Test-Path $hostDll) { Move-Item $hostDll "$hostDll.server-only" -Force; Warn 'SFHeadlessHost.dll movido (solo va en el servidor).' }

# ----------------------------------------------------- activar BepInEx doorstop
Step 6 'Activando BepInEx (doorstop)...'
if (Test-Path $doorstop) {
    $ini = Get-Content $doorstop -Raw
    $ini = $ini -replace 'enabled\s*=\s*false', 'enabled = true'
    Set-Content $doorstop $ini -NoNewline
    Ok 'doorstop enabled = true.'
}
# Si hay MelonLoader, BepInEx (winhttp) y Melon (version) pueden convivir; avisamos
if ($hasMelon) {
    Warn 'Tenes MelonLoader + BepInEx. Si el menu online falla, renombra version.dll temporalmente.'
}

# ----------------------------------------------------- launch options + acceso directo
Step 7 'Dejando opciones de lanzamiento y acceso directo...'
$launchArgs = ("-address {0} -port {1}" -f $ServerIp, $ServerPort)
$desktop = [Environment]::GetFolderPath('Desktop')
$batPath = Join-Path $desktop 'Jugar-StickFight-ALKA.bat'
@"
@echo off
title ALKA-KITSLAYER  Stick Fight Oracle
rem Menu SERVERS en el juego (listar + unirse a lobbies de este servidor):
set "SF_LOBBY_ENDPOINT=http://${ServerIp}:8080/lobbies"
rem Para CREAR lobbies desde el juego necesitas el token del servidor:
rem   set "SF_CONTROL_TOKEN=pide-el-token-al-operador"
start "" "$Sf\StickFight.exe" $launchArgs
"@ | Set-Content -Path $batPath -Encoding ASCII
Ok ("Acceso directo: {0}" -f $batPath)

Write-Host ''
Write-Host '===================================================================' -ForegroundColor Green
Write-Host '   INSTALACION COMPLETA  -  ALKA-KITSLAYER' -ForegroundColor Yellow
Write-Host '===================================================================' -ForegroundColor Green
Write-Host ''
Write-Host '  Para jugar (cualquiera de las dos):' -ForegroundColor White
Write-Host ('   A) Doble clic en el escritorio: Jugar-StickFight-ALKA.bat') -ForegroundColor Gray
Write-Host  '   B) Steam -> Stick Fight -> Propiedades -> Opciones de lanzamiento:' -ForegroundColor Gray
Write-Host ('        {0}' -f $launchArgs) -ForegroundColor Cyan
Write-Host ''
Write-Host '  En el juego: PLAY ONLINE -> QUICK MATCH. En el lobby escribi /start' -ForegroundColor White
Write-Host ''
Write-Host '  Revertir todo: DESINSTALAR-ALKA-KITSLAYER.bat' -ForegroundColor DarkGray
Write-Host ''
Write-Host '  Gracias por jugar en ALKA-KITSLAYER !' -ForegroundColor Magenta
Write-Host ''
