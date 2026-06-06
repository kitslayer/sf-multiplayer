# Instala el cliente para oracle de forma PERSISTENTE (Steam o .bat).
# Requiere: Assembly-CSharp.srv.v25.dll, BepInEx, SFClientRecon (sin SFHeadlessHost en el cliente).
# Team: kitslayer + AlkaDev
param(
    [string]$ServerIp = '69.53.117.43',
    [string]$ServerPort = '1337',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'team-sf-multiplayer.ps1')
Show-SfMultiplayerTeamInfo
$RepoRoot = Split-Path $PSScriptRoot -Parent
$Sf = Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\StickFightTheGame'
$SrvDll = Join-Path $RepoRoot 'client-mod\dll\Assembly-CSharp.srv.v25.dll'
$Managed = Join-Path $Sf 'StickFight_Data\Managed'
$TargetAsm = Join-Path $Managed 'Assembly-CSharp.dll'
$VanillaBak = Join-Path $Managed 'Assembly-CSharp.dll.vanilla.bak'
$Plug = Join-Path $Sf 'BepInEx\plugins'

if (-not (Test-Path (Join-Path $Sf 'StickFight.exe'))) {
    throw ('No encuentro Stick Fight en ' + $Sf)
}
if (-not (Test-Path $SrvDll)) {
    throw ('Falta ' + $SrvDll + ' — copia Assembly-CSharp.srv.v25.dll a client-mod\dll\')
}

if (-not $SkipBuild) {
    & (Join-Path $RepoRoot 'deploy-physics-fix.ps1') -InstallLocal
}

# --- Mod UDP (obligatorio) ---
if (-not (Test-Path $VanillaBak)) {
    Write-Host '[1/4] Backup vanilla Assembly-CSharp.dll...'
    Copy-Item $TargetAsm $VanillaBak -Force
}
$vanillaHash = (Get-FileHash $TargetAsm).Hash
$srvHash = (Get-FileHash $SrvDll).Hash
if ($vanillaHash -eq $srvHash) {
    Write-Host '[1/4] Assembly-CSharp.srv.v25 ya activo.'
} else {
    Write-Host '[1/4] Instalando Assembly-CSharp.srv.v25 como Assembly-CSharp.dll...'
    Copy-Item $SrvDll $TargetAsm -Force
}

# --- BepInEx ON (sin esto Steam ignora SFClientRecon) ---
Write-Host '[2/4] Activando BepInEx (doorstop)...'
$iniPath = Join-Path $Sf 'doorstop_config.ini'
$ini = Get-Content $iniPath -Raw
$ini = $ini -replace 'enabled = false', 'enabled = true'
Set-Content $iniPath $ini -NoNewline

# --- Solo plugin de cliente en la PC del jugador ---
Write-Host '[3/4] Plugins BepInEx (solo SFClientRecon en cliente)...'
$hostDll = Join-Path $Plug 'SFHeadlessHost.dll'
if (Test-Path $hostDll) {
    $hostOff = Join-Path $Plug 'SFHeadlessHost.dll.oracle-client-off'
    if (-not (Test-Path $hostOff)) { Move-Item $hostDll $hostOff -Force }
    Write-Host '      SFHeadlessHost.dll movido fuera (solo va en el VPS).'
}
if (-not (Test-Path (Join-Path $Plug 'SFClientRecon.dll'))) {
    throw 'Falta SFClientRecon.dll — ejecuta deploy-physics-fix.ps1 -InstallLocal'
}

# --- MelonLoader: no obligatorio desactivarlo; aviso si compite ---
if (Test-Path (Join-Path $Sf 'MelonLoader')) {
    Write-Host '[4/4] AVISO: MelonLoader presente. Si el menu falla, usa jugar-oracle.ps1 (desactiva Melon solo esa sesion).'
} else {
    Write-Host '[4/4] Sin MelonLoader — OK para oracle.'
}

Write-Host ''
Write-Host '=== Cliente oracle listo ===' -ForegroundColor Green
Write-Host ('Steam -> Stick Fight -> Propiedades -> Opciones de lanzamiento:')
Write-Host ('  -address ' + $ServerIp + ' -port ' + $ServerPort)
Write-Host ''
Write-Host 'Abre desde Steam, en el menu: QUICK MATCH (o HOST MATCH).'
Write-Host 'En el lobby del mapa: /start en el chat.'
Write-Host ''
Write-Host 'Para volver al juego vanilla + Melon: deploy\restaurar-juego-normal.ps1'
Write-Host ''
$activeHash = (Get-FileHash $TargetAsm).Hash
$srvHash = (Get-FileHash $SrvDll).Hash
if ($activeHash -eq $srvHash) {
    Write-Host 'OK: Assembly-CSharp.srv.v25 activo.' -ForegroundColor Green
} else {
    Write-Host 'AVISO: Assembly-CSharp.dll NO coincide con srv.v25 — revisa copia.' -ForegroundColor Yellow
}
