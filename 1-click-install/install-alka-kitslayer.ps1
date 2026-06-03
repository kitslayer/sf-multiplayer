# ===================================================================
#  ALKA-KITSLAYER  -  Stick Fight: The Game  -  Oracle 1-Click Installer
#  Team: kitslayer + AlkaDev  |  GitHub: kitslayer, AlkaPrime12
#
#  What it does (without breaking your folder):
#   1. Finds Stick Fight in any Steam library.
#   2. BACKS UP your Assembly-CSharp.dll and doorstop_config.ini.
#   3. Detects your loader: BepInEx and/or MelonLoader, and adapts.
#   4. Installs BepInEx (if missing), the patched Assembly-CSharp and the
#      SFClientRecon (+ SFServerBrowser) plugins.
#   5. Sets the launch options (-address of the oracle).
#   6. Creates "Jugar-StickFight-ALKA.bat" on the desktop.
#
#  To revert: run DESINSTALAR-ALKA-KITSLAYER.bat (restores the backup).
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

Step 1 'Searching for Stick Fight: The Game...'
$Sf = Find-StickFight
if (-not $Sf) {
    Write-Host ''
    Warn 'Could not find Stick Fight automatically.'
    $Sf = Read-Host '    Paste the path to your StickFightTheGame folder'
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
Step 2 'Detecting loader (BepInEx / MelonLoader)...'
$hasBepInEx = (Test-Path (Join-Path $Sf 'BepInEx')) -or (Test-Path (Join-Path $Sf 'winhttp.dll'))
$hasMelon   = (Test-Path (Join-Path $Sf 'MelonLoader')) -or (Test-Path (Join-Path $Sf 'version.dll'))
if ($hasMelon) { Warn 'MelonLoader detected.' } else { Ok 'No MelonLoader.' }
if ($hasBepInEx) { Ok 'BepInEx already present.' } else { Warn 'BepInEx missing: installing it.' }

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
        Ok 'BepInEx installed.'
    } catch {
        throw ("No pude descargar BepInEx automaticamente: {0}. Instalalo manual y reintenta." -f $_.Exception.Message)
    }
}

# ----------------------------------------------------- backup (no romper nada)
Step 3 'Backing up your original files...'
if (-not (Test-Path $VanillaBak)) {
    if (Test-Path $TargetAsm) { Copy-Item $TargetAsm $VanillaBak -Force; Ok 'Backed up your Assembly-CSharp.dll (.vanilla.bak).' }
} else { Ok 'Backup already exists (not overwriting).' }
$doorstop = Join-Path $Sf 'doorstop_config.ini'
if ((Test-Path $doorstop) -and -not (Test-Path "$doorstop.alka.bak")) {
    Copy-Item $doorstop "$doorstop.alka.bak" -Force
}

# ----------------------------------------------------- instalar parche + plugins
Step 4 'Installing patched Assembly-CSharp (lobby -> oracle)...'
Copy-Item $SrvDll $TargetAsm -Force
Ok 'Assembly-CSharp.srv.v25 active.'

Step 5 'Installing plugins into BepInEx...'
if (-not (Test-Path $Plug)) { New-Item -ItemType Directory -Path $Plug -Force | Out-Null }
Copy-Item $ReconDll (Join-Path $Plug 'SFClientRecon.dll') -Force
Ok 'SFClientRecon.dll'
if (Test-Path $BrowDll) { Copy-Item $BrowDll (Join-Path $Plug 'SFServerBrowser.dll') -Force; Ok 'SFServerBrowser.dll' }
# El host NUNCA va en la PC del jugador
$hostDll = Join-Path $Plug 'SFHeadlessHost.dll'
if (Test-Path $hostDll) { Move-Item $hostDll "$hostDll.server-only" -Force; Warn 'SFHeadlessHost.dll moved (server-only).' }

# ----------------------------------------------------- activar BepInEx doorstop
Step 6 'Enabling BepInEx (doorstop)...'
if (Test-Path $doorstop) {
    $ini = Get-Content $doorstop -Raw
    $ini = $ini -replace 'enabled\s*=\s*false', 'enabled = true'
    Set-Content $doorstop $ini -NoNewline
    Ok 'doorstop enabled = true.'
}
# Si hay MelonLoader, BepInEx (winhttp) y Melon (version) pueden convivir; avisamos
if ($hasMelon) {
    Warn 'You have MelonLoader + BepInEx. If the online menu fails, temporarily rename version.dll.'
}

# ----------------------------------------------------- launch options + acceso directo
Step 7 'Setting launch options and desktop shortcut...'
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
Ok ("Shortcut: {0}" -f $batPath)

Write-Host ''
Write-Host '===================================================================' -ForegroundColor Green
Write-Host '   INSTALL COMPLETE  -  ALKA-KITSLAYER' -ForegroundColor Yellow
Write-Host '===================================================================' -ForegroundColor Green
Write-Host ''
Write-Host '  To play (either way):' -ForegroundColor White
Write-Host ('   A) Double-click on the desktop: Jugar-StickFight-ALKA.bat') -ForegroundColor Gray
Write-Host  '   B) Steam -> Stick Fight -> Properties -> Launch options:' -ForegroundColor Gray
Write-Host ('        {0}' -f $launchArgs) -ForegroundColor Cyan
Write-Host ''
Write-Host '  In game: PLAY ONLINE -> QUICK MATCH. In the lobby type /start' -ForegroundColor White
Write-Host ''
Write-Host '  Roll back everything: DESINSTALAR-ALKA-KITSLAYER.bat' -ForegroundColor DarkGray
Write-Host ''
Write-Host '  Thanks for playing ALKA-KITSLAYER!' -ForegroundColor Magenta
Write-Host ''
