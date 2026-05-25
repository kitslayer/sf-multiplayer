# Sesion oracle: instala cliente persistente, MelonLoader OFF solo esta sesion, lanza el juego.
# Equipo: kitslayer + AlkaDev | Discord: kitslayer, Tyralka0660
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'equipo-sf-multiplayer.ps1')
Show-SfMultiplayerTeamInfo
$RepoRoot = Split-Path $PSScriptRoot -Parent
$Sf = Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\StickFightTheGame'
$Ip = '69.53.117.43'
$Port = '1337'

Set-Location $Sf
if (-not (Test-Path 'StickFight.exe')) {
    throw ('No encuentro Stick Fight en ' + $Sf)
}

& (Join-Path $PSScriptRoot 'instalar-oracle-desde-cero.ps1') -ServerIp $Ip -ServerPort $Port
$plug = Join-Path $Sf 'BepInEx\plugins'
$pending = Join-Path $plug 'SFClientRecon.new.dll'
if (Test-Path $pending) {
    Copy-Item $pending (Join-Path $plug 'SFClientRecon.dll') -Force
    Remove-Item $pending -Force
}

# --- MelonLoader off solo esta sesion ---
$renamed = @()
foreach ($dir in @('MelonLoader', 'Mods', 'Plugins')) {
    if (Test-Path $dir) {
        $off = $dir + '.off.oracle'
        if (Test-Path $off) { Remove-Item $off -Recurse -Force }
        Rename-Item $dir $off
        $renamed += $dir
    }
}

if (-not (Test-Path (Join-Path $plug 'SFClientRecon.dll'))) {
    Write-Host 'AVISO: SFClientRecon.dll no esta en BepInEx\plugins. Ejecuta deploy-physics-fix.ps1 -InstallLocal' -ForegroundColor Yellow
}

Write-Host ''
Write-Host ('Oracle ' + $Ip + ':' + $Port) -ForegroundColor Cyan
Write-Host 'En el MENU del juego: pulsa QUICK MATCH o HOST MATCH (ya no usa Steam).'
Write-Host 'En el lobby del mapa: escribe /start en el CHAT para empezar la ronda.'
Write-Host 'NO uses el menu "Host game" de Steam.'
Write-Host ''

try {
    $exe = Join-Path $Sf 'StickFight.exe'
    $p = Start-Process -FilePath $exe -ArgumentList '-address', $Ip, '-port', $Port -WorkingDirectory $Sf -PassThru -Wait
    Write-Host ('Salida: ' + $p.ExitCode)
}
finally {
    foreach ($dir in $renamed) {
        $off = $dir + '.off.oracle'
        if (Test-Path $off) { Rename-Item $off $dir }
    }
    Write-Host 'MelonLoader / Mods restaurados. Assembly srv y BepInEx siguen instalados (usa Steam con -address/-port).'
}
